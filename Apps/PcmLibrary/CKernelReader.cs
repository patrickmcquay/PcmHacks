using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PcmHacking
{
    /// <summary>
    /// Reader classes use a kernel to read the entire flash memory.
    /// </summary>
    public class CKernelReader
    {
        private readonly Vehicle vehicle;
        private readonly PcmInfo pcmInfo;
        private readonly Protocol protocol;
        private readonly ILogger logger;

        public CKernelReader(Vehicle vehicle, PcmInfo pcmInfo, ILogger logger)
        {
            this.vehicle = vehicle;
            this.pcmInfo = pcmInfo;

            // This seems wrong... Some alternatives:
            // a) Have the caller pass in the message factory and message-parser methods
            // b) Have the caller pass in a smaller KernelProtocol class - with subclasses for each kernel - 
            //    This would only make sense if it turns out that this one reader class can handle multiple kernels.
            // c) Just create a smaller KernelProtocol class here, for the kernel that this class is intended for.
            this.protocol = new Protocol();

            this.logger = logger;
        }

        /// <summary>
        /// Read the full contents of the PCM.
        /// Assumes the PCM is unlocked and we're ready to go.
        /// </summary>
        public async Task<Stream> ReadContents(CancellationToken cancellationToken)
        {
            try
            {
                // Start with known state.
                await this.vehicle.ForceSendToolPresentNotification();
                this.vehicle.ClearDeviceMessageQueue();

                // Switch to 4x, if possible. But continue either way.
                if (this.vehicle.Enable4xReadWrite)
                {
                    // if the vehicle bus switches but the device does not, the bus will need to time out to revert back to 1x, and the next steps will fail.
                    await this.vehicle.VehicleSetVPW4x();
                }
                else
                {
                    this.logger.AddUserMessage("4X communications disabled by configuration.");
                }

                await this.vehicle.SendToolPresentNotification();

                byte[] file;

                // Execute kernel loader, if required
                if (this.pcmInfo.LoaderRequired)
                {
                    try
                    {
                        file = await vehicle.LoadKernelFromFile(this.pcmInfo.LoaderFileName);
                    }
                    catch (Exception exception)
                    {
                        logger.AddUserMessage("Failed to load loader from file.");
                        logger.AddUserMessage(exception.Message);
                        return null;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }

                    await this.vehicle.SendToolPresentNotification();

                    if (!await this.vehicle.PCMExecute(this.pcmInfo, file, cancellationToken))
                    {
                        throw new ObdException("Failed to upload loader to PCM", cancellationToken.IsCancellationRequested ? ObdExceptionReason.Cancelled : ObdExceptionReason.Error);
                    }

                    logger.AddUserMessage("Loader uploaded to PCM succesfully.");
                }

                // execute read kernel
                try
                {
                    file = await vehicle.LoadKernelFromFile(this.pcmInfo.KernelFileName);
                }
                catch (Exception exception)
                {
                    logger.AddUserMessage("Failed to load kernel from file.");
                    logger.AddUserMessage(exception.Message);
                    return null;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                await this.vehicle.SendToolPresentNotification();

                if (!await this.vehicle.PCMExecute(this.pcmInfo, file, cancellationToken))
                {
                    throw new ObdException("Failed to upload kernel to PCM", cancellationToken.IsCancellationRequested ? ObdExceptionReason.Cancelled : ObdExceptionReason.Error);
                }

                logger.AddUserMessage("Kernel uploaded to PCM succesfully. Requesting data...");

                // Which flash chip?
                await this.vehicle.SendToolPresentNotification();

                FlashChip flashChip = FlashChip.Create(0x12345678, this.logger);
                if (this.pcmInfo.FlashIDSupport)
                {
                    UInt32 chipId = await this.vehicle.QueryFlashChipId(cancellationToken);
                    flashChip = FlashChip.Create(chipId, this.logger);
                    logger.AddUserMessage("Flash chip: " + flashChip.ToString());
                }

                await this.vehicle.SetDeviceTimeout(TimeoutScenario.ReadMemoryBlock);

                byte[] image = new byte[pcmInfo.ImageSize];
                int totalRetryCount = 0;
                int startAddress = 0;
                int bytesRemaining = pcmInfo.ImageSize;
                int blockSize = this.vehicle.DeviceMaxReceiveSize - 10 - 2; // allow space for the header and block checksum

                if (blockSize > this.pcmInfo.KernelMaxBlockSize)
                {
                    blockSize = this.pcmInfo.KernelMaxBlockSize;
                }

                DateTime startTime = DateTime.MaxValue;
                while (startAddress < pcmInfo.ImageSize)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }

                    // The read kernel needs a short message here for reasons unknown. Without it, it will RX 2 messages then drop one.
                    await this.vehicle.ForceSendToolPresentNotification();

                    if (startAddress + blockSize > pcmInfo.ImageSize)
                    {
                        blockSize = pcmInfo.ImageSize - startAddress;
                    }

                    if (blockSize < 1)
                    {
                        this.logger.AddUserMessage("Image download complete");
                        break;
                    }

                    if (startTime == DateTime.MaxValue)
                    {
                        startTime = DateTime.Now;
                    }

                    this.logger.AddDebugMessage(string.Format("Reading from {0} / 0x{0:X}, length {1} / 0x{1:X}", startAddress, blockSize));

                    for (int blockRetryCount = 0; blockRetryCount < Vehicle.MaxSendAttempts && !cancellationToken.IsCancellationRequested; blockRetryCount++)
                    {
                        if (await TryReadBlock(image, blockSize, startAddress, startTime, cancellationToken))
                        {
                            break; 
                        }

                        if (blockRetryCount == Vehicle.MaxSendAttempts)
                        {
                            throw new ObdException($"Tried to read block {Vehicle.MaxSendAttempts} times, read failed.", ObdExceptionReason.Error);
                        }

                        logger.StatusUpdateRetryCount((totalRetryCount > 0) ? totalRetryCount.ToString() + ((totalRetryCount > 1) ? " Retries" : " Retry") : string.Empty);
                        totalRetryCount++;
                    }

                    startAddress += blockSize;
                }

                logger.AddUserMessage("Read complete.");
                Utility.ReportRetryCount("Read", totalRetryCount, pcmInfo.ImageSize, this.logger);

                if (this.pcmInfo.FlashCRCSupport && this.pcmInfo.FlashIDSupport)
                {
                    logger.AddUserMessage("Starting verification...");

                    CKernelVerifier verifier = new CKernelVerifier(
                        image,
                        flashChip.MemoryRanges,
                        this.vehicle,
                        this.protocol,
                        this.pcmInfo,
                        this.logger);

                    logger.StatusUpdateReset();

                    if (await verifier.CompareRanges(
                        image,
                        BlockType.All,
                        cancellationToken))
                    {
                        logger.AddUserMessage("The contents of the file match the contents of the PCM.");
                    }
                    else
                    {
                        logger.AddUserMessage("##############################################################################");
                        logger.AddUserMessage("There are errors in the data that was read from the PCM. Do not use this file.");
                        logger.AddUserMessage("##############################################################################");
                    }
                }

                await this.vehicle.Cleanup(); // Not sure why this does not get called in the finally block on successfull read?

                MemoryStream stream = new MemoryStream(image);
                return stream;
            }
            catch(Exception exception)
            {
                throw new ObdException("Something went wrong.", ObdExceptionReason.Error, exception);
            }
            finally
            {
                // Sending the exit command at both speeds and revert to 1x.
                await this.vehicle.Cleanup();
                logger.StatusUpdateReset();
            }
        }

        /// <summary>
        /// Try to read a block of PCM memory.
        /// </summary>
        private async Task<bool> TryReadBlock(
            byte[] image, 
            int length, 
            int startAddress, 
            DateTime startTime,
            CancellationToken cancellationToken)
        {


            byte[] readResponse = await this.vehicle.ReadMemory(
                () => this.protocol.CreateReadRequest(startAddress, length),
                (payloadMessage) => this.protocol.ParsePayload(payloadMessage, length, startAddress),
                cancellationToken);

            byte[] payload = readResponse;

            if (payload.Length != length)
            {
                throw new ObdException($"Expected {length} bytes, received {payload.Length} bytes.", ObdExceptionReason.Truncated);
            }

            Buffer.BlockCopy(payload, 0, image, startAddress, payload.Length);

            TimeSpan elapsed = DateTime.Now - startTime;
            string timeRemaining = string.Empty;

            UInt32 bytesPerSecond = 0;
            UInt32 bytesRemaining = 0;

            bytesPerSecond = (UInt32)(startAddress / elapsed.TotalSeconds);
            bytesRemaining = (UInt32)(image.Length - startAddress);

            // Don't divide by zero.
            if (bytesPerSecond > 0)
            {
                UInt32 secondsRemaining = (UInt32)(bytesRemaining / bytesPerSecond);
                timeRemaining = TimeSpan.FromSeconds(secondsRemaining).ToString("mm\\:ss");
            }

            logger.StatusUpdateActivity($"Reading {payload.Length} bytes from 0x{startAddress:X6}");
            logger.StatusUpdatePercentDone((startAddress * 100 / image.Length > 0) ? $"{startAddress * 100 / image.Length}%" : string.Empty);
            logger.StatusUpdateTimeRemaining($"T-{timeRemaining}");
            logger.StatusUpdateKbps((bytesPerSecond > 0) ? $"{(double)bytesPerSecond * 8.00 / 1000.00:0.00} Kbps" : string.Empty);
            logger.StatusUpdateProgressBar((double)(startAddress + payload.Length) / image.Length, true);

            return true;
            
        }
    }
}
