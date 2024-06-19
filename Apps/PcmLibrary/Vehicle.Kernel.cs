﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PcmHacking
{
    /// <summary>
    /// From the application's perspective, this class is the API to the vehicle.
    /// </summary>
    /// <remarks>
    /// Methods in this class are high-level operations like "get the VIN," or "read the contents of the EEPROM."
    /// </remarks>
    public partial class Vehicle : IDisposable
    {
        /// <summary>
        /// Suppres chatter on the VPW bus.
        /// </summary>
        public async Task SuppressChatter()
        {
            this.logger.AddDebugMessage("Suppressing VPW chatter.");
            Message suppressChatter = this.protocol.CreateDisableNormalMessageTransmission();
            await this.device.SendMessage(suppressChatter);
            await this.notifier.ForceNotify();

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            while (stopwatch.ElapsedMilliseconds < 1000)
            {
                Message received = await this.device.ReceiveMessage();
                if (received != null)
                {
                    this.logger.AddDebugMessage("Ignoring chatter: " + received.ToString());
                    break;
                }
                else
                {
                    await Task.Delay(100);
                }
            }
        }

        /// <summary>
        /// Writes a block of data to the PCM
        /// Requires an unlocked PCM
        /// </summary>
        private async Task WriteBlock(byte block, byte[] data)
        {
            if (data.Length != 6)
            {
                throw new ObdException("Cant write block size " + data.Length, ObdExceptionReason.Error);
            }

            Message m = new Message(new byte[] { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, 0x3B, block, data[0], data[1], data[2], data[3], data[4], data[5] });
            await this.device.SendMessage(m);

            logger.AddDebugMessage("Successful write to block " + block);
        }

        /// <summary>
        /// Opens the named kernel file. The file must be in the same directory as the EXE.
        /// </summary>
        public async Task<byte[]> LoadKernelFromFile(string path)
        {
            byte[] file = { 0x00 }; // dummy value

            if (path == "")
            {
                throw new InvalidOperationException("File name not provided.");
            }

            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string exeDirectory = Path.GetDirectoryName(exePath);
            path = Path.Combine(exeDirectory, path);

            using (Stream fileStream = File.OpenRead(path))
            {
                if (fileStream.Length == 0)
                {
                    throw new InvalidDataException("invalid kernel image (zero bytes). " + path);
                }
                file = new byte[fileStream.Length];

                // In theory we might need a loop here. In practice, I don't think that will be necessary.
                int bytesRead = await fileStream.ReadAsync(file, 0, (int)fileStream.Length);

                if(bytesRead != fileStream.Length)
                {
                    throw new InvalidDataException("Unable to read entire file.");
                }
            }

            logger.AddDebugMessage("Loaded " + path);
            return file;
        }

        /// <summary>
        /// Cleanup calls the various cleanup routines to get everything back to normal
        /// </summary>
        /// <remarks>
        /// Exit kernel at 4x, 1x, and clear DTCs
        /// </remarks>
        public async Task Cleanup()
        {
            this.logger.AddDebugMessage("Halting the kernel.");
            await this.ExitKernel();
            await this.ClearTroubleCodes();
        }

        /// <summary>
        /// Exits the kernel at 4x, then at 1x. Once this function has been called the bus will be back at 1x.
        /// </summary>
        /// <remarks>
        /// Can be used to force exit the kernel, if requied. Does not attempt the 4x exit if not supported by the current device.
        /// </remarks>
        public async Task ExitKernel()
        {
            Message exitKernel = this.protocol.CreateExitKernel();

            this.device.ClearMessageQueue();
            if (device.Supports4X)
            {
                await device.SetVpwSpeed(VpwSpeed.FourX);
                await this.device.SendMessage(exitKernel);
                await device.SetVpwSpeed(VpwSpeed.Standard);
            }

            await this.device.SendMessage(exitKernel);
        }

        /// <summary>
        /// Ask the factory operating system to clear trouble codes. 
        /// In theory this should only run 10 seconds after rebooting, to ensure that the operating system is running again.
        /// In practice, that hasn't been an issue. It's the other modules (TAC especially) that really need to be reset.
        /// </summary>
        public async Task ClearTroubleCodes()
        {
            this.logger.AddUserMessage("Clearing trouble codes.");
            this.device.ClearMessageQueue();

            // No timeout because we don't care about responses to these messages.
            await this.device.SetTimeout(TimeoutScenario.Minimum);

            // The response is not checked because the priority byte and destination address are odd.
            // Different devices will handle this differently. Scantool won't recieve it.
            // so we send it twice just to be sure.
            Message clearCodesRequest = this.protocol.CreateClearDiagnosticTroubleCodesRequest();

            await Task.Delay(250);
            await this.device.SendMessage(clearCodesRequest);
            await Task.Delay(250);
            await this.device.SendMessage(clearCodesRequest);

            // This is a conventional message, but the response from the PCM might get lost 
            // among the responses from other modules on the bus, so again we just send it twice.
            Message clearDiagnosticInformationRequest = this.protocol.CreateClearDiagnosticInformationRequest();

            await Task.Delay(250);
            await this.device.SendMessage(clearDiagnosticInformationRequest);
            await Task.Delay(250);
            await this.device.SendMessage(clearDiagnosticInformationRequest);
        }

        /// <summary>
        /// Query the PCM's operating system ID.
        /// </summary>
        /// <returns></returns>
        public async Task<UInt32> QueryOperatingSystemIdFromKernel(CancellationToken cancellationToken)
        {
            await this.device.SetTimeout(TimeoutScenario.ReadProperty);

            var query = this.CreateQuery(
                this.protocol.CreateOperatingSystemIdKernelRequest,
                this.protocol.ParseOperatingSystemIdKernelResponse,
                CancellationToken.None);

            return await query.Execute();
        }

        /// <summary>
        /// Ask the kernel for the ID of the flash chip.
        /// </summary>
        public async Task<UInt32> QueryFlashChipId(CancellationToken cancellationToken)
        {
            for (int retries = 0; retries < 3; retries++)
            {
                await this.SetDeviceTimeout(TimeoutScenario.ReadProperty);

                Query<UInt32> chipIdQuery = this.CreateQuery<UInt32>(
                    this.protocol.CreateFlashMemoryTypeQuery,
                    this.protocol.ParseFlashMemoryType,
                    cancellationToken);
                

                UInt32 chipIdResponse = await chipIdQuery.Execute();

                if (chipIdResponse == 0)
                {
                    continue;
                }

                return chipIdResponse;
            }

            logger.AddUserMessage("Unable to determine which flash chip is in this PCM");

            return 0;
        }

        /// <summary>
        /// Check for a running kernel.
        /// </summary>
        /// <returns></returns>
        public async Task<UInt32> GetKernelVersion()
        {
            Message query = this.protocol.CreateKernelVersionQuery();

            for (int retryCount = 0; retryCount < 5; retryCount++)
            {
                await this.device.SendMessage(query);

                Message reply = await this.device.ReceiveMessage();
                if (reply == null)
                {
                    await Task.Delay(100);
                    continue;
                }

                var response = this.protocol.ParseKernelVersion(reply);
                if (response != 0)
                {
                    return response;
                }

                await Task.Delay(100);
            }

            return 0;
        }

        /// <summary>
        /// Load the executable payload on the PCM at the supplied address, and execute it.
        /// </summary>
        public async Task<bool> PCMExecute(PcmInfo info, byte[] payload, CancellationToken cancellationToken)
        {
            var loaderType = info.LoaderRequired ? "loader" : "kernel";

            // Note that we request an upload of 4k maximum, because the PCM will reject anything bigger.
            // But you can request a 4k upload and then send up to 16k if you want, and the PCM will not object.
            int claimedSize = Math.Min(4096, payload.Length);

            // Since we're going to lie about the size, we need to check for overflow ourselves.
            if (info.HardwareType == PcmType.P01_P59 && info.KernelBaseAddress + payload.Length > 0xFFCDFF)
            {
                logger.AddUserMessage("Base address and size would exceed usable RAM.");
                return false;
            }

            int loadAddress;

            if (info.LoaderRequired)
            {
                loadAddress = info.LoaderBaseAddress;
                logger.AddUserMessage("PCM uses a kernel loader.");
            }
            else
            {
                loadAddress = info.KernelBaseAddress;
            }

            logger.AddDebugMessage($"Sending upload request for {loaderType} size {payload.Length}, loadaddress {loadAddress:X6}");

            Query<bool> uploadPermissionQuery = new Query<bool>(
                this.device,
                () => protocol.CreateUploadRequest(info, claimedSize),
                (message) => protocol.IsUploadPermissionResponseValid(info, message),
                this.logger,
                cancellationToken,
                this.notifier);

            if (!await uploadPermissionQuery.Execute())
            {
                throw new ObdException($"Permission to upload {loaderType} was denied. {Environment.NewLine} If this persists, try cutting power to the PCM, restoring power, waiting ten seconds, and trying again.", ObdExceptionReason.Refused);
            }

            logger.AddUserMessage("Upload permission granted.");
            logger.AddDebugMessage($"Going to load a {payload.Length} byte {loaderType} to 0x{loadAddress.ToString("X6")}");

            await this.device.SetTimeout(TimeoutScenario.SendKernel);

            // Loop through the payload building and sending packets, highest first, execute on last
            int payloadSize = device.MaxKernelSendSize - 12; // Headers use 10 bytes, sum uses 2 bytes.
            if (info.LoaderBaseAddress > 0 && loadAddress == info.KernelBaseAddress)
            {
                payloadSize = 512;  // If we are using a loader kernel use a small packet size due to limited resources.
            }
            int chunkCount = payload.Length / payloadSize;
            int remainder = payload.Length % payloadSize;

            int offset = (chunkCount * payloadSize);
            int startAddress = loadAddress + offset;

            // First we send the 'remainder' payload, containing any bytes that won't fill up an entire upload packet.
            logger.AddDebugMessage($"Sending end block payload with offset 0x{offset:X}, start address 0x{startAddress:X}, length 0x{remainder:X}.");

            Message remainderMessage = protocol.CreateBlockMessage(
                payload, 
                offset, 
                remainder,
                loadAddress + offset, 
                remainder == payload.Length ? BlockCopyType.Execute : BlockCopyType.Copy);

            await notifier.Notify();

            await WritePayload(info, remainderMessage, cancellationToken);

            // Now we send a series of full upload packets
            // Note that there's a notifier.Notify() call inside the WritePayload() call in this loop.
            for (int chunkIndex = chunkCount; chunkIndex > 0; chunkIndex--)
            {
                int bytesSent = payload.Length - offset;
                int percentDone = bytesSent * 100 / payload.Length;

                this.logger.AddUserMessage($"{loaderType} upload {percentDone}% complete.");

                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                offset = (chunkIndex - 1) * payloadSize;
                startAddress = loadAddress + offset;

                Message payloadMessage = protocol.CreateBlockMessage(
                    payload,
                    offset,
                    payloadSize,
                    startAddress,
                    offset == 0 ? BlockCopyType.Execute : BlockCopyType.Copy);

                logger.AddDebugMessage($"Sending block with offset 0x{offset:X6}, start address 0x{startAddress:X6}, length 0x{payloadSize:X4}.");

                await WritePayload(info, payloadMessage, cancellationToken);
            }

            this.logger.AddUserMessage($"{loaderType} upload 100% complete.");

            if (ReportKernelID && info.KernelVersionSupport)
            {
                // Consider: Allowing caller to call GetKernelVersion(...)?
                // Consider: return kernel version rather than boolean?
                UInt32 kernelVersion = await this.GetKernelVersion();
                if (kernelVersion == 0)
                {
                    this.logger.AddUserMessage($"{loaderType} failed to start.");
                    return false;
                }
                this.logger.AddUserMessage($"{loaderType} Version: {kernelVersion.ToString("X8")}");

                // Detect an Assemply Kernel, // Remove with the C Kernels
                if (kernelVersion > 0x82400000)
                {
                    info.AssemblyKernel = true;
                }
            }

            if (info.LoaderRequired)
            {
                // Switch modes to Kernel, Loader is already on PCM.
                // It has outlived it's usefulness, so use it for Loader vs Kernel state switch.
                info.LoaderRequired = false;
            }

            return true;
        }

        /// <summary>
        /// Does everything required to switch to VPW 4x
        /// </summary>
        public async Task VehicleSetVPW4x()
        {
            if (!device.Supports4X) 
            {
                throw new ObdException("This device does not support 4X mode.", ObdExceptionReason.Error);
            }

            if (!this.device.Enable4xReadWrite)
            {
                throw new ObdException("4X communications disabled by configuration.", ObdExceptionReason.Error);
            }

            // Configure the vehicle bus when switching to 4x
            logger.AddUserMessage("Attempting switch to VPW 4x");
            await device.SetTimeout(TimeoutScenario.ReadProperty);

            // The list of modules may not be useful after all, but 
            // checking for an empty list indicates an uncooperative
            // module on the VPW bus.
            List<byte> modules = await this.RequestHighSpeedPermission(notifier);
            if (modules == null)
            {
                // A device has refused the switch to high speed mode.
                throw new ObdException("A device has refused the switch to high speed mode.", ObdExceptionReason.Error);
            }

            // Since we had some issue with other modules not staying quiet...
            await this.ForceSendToolPresentNotification();

            Message broadcast = this.protocol.CreateBeginHighSpeed(DeviceId.Broadcast);
            await this.device.SendMessage(broadcast);

            // Check for any devices that refused to switch to 4X speed.
            // These responses usually get lost, so this code might be pointless.
            Stopwatch sw = new Stopwatch();
            sw.Start();
            Message response = null;

            // WARNING: The AllPro stopped receiving permission-to-upload messages when this timeout period
            // was set to 1500ms.  Reducing it to 500 seems to have fixed that problem. 
            // 
            // It would be nice to find a way to wait equally long with all devices, as refusal messages
            // are still a potetial source of trouble. 
            while (((response = await this.device.ReceiveMessage()) != null) && (sw.ElapsedMilliseconds < 500))
            {
                if (this.protocol.ParseHighSpeedRefusal(response))
                {
                    // TODO: Add module number.
                    throw new ObdException("Module refused high-speed switch.", ObdExceptionReason.Error);
                }

                // This should help ELM devices receive responses.
                await Task.Delay(100);
                await notifier.ForceNotify();
            }

            // Request the device to change
            await device.SetVpwSpeed(VpwSpeed.FourX);

            // Since we had some issue with other modules not staying quiet...
            await this.ForceSendToolPresentNotification();
        }

        /// <summary>
        /// Ask all of the devices on the VPW bus for permission to switch to 4X speed.
        /// </summary>
        private async Task<List<byte>> RequestHighSpeedPermission(ToolPresentNotifier notifier)
        {
            Message permissionCheck = this.protocol.CreateHighSpeedPermissionRequest(DeviceId.Broadcast);
            await this.device.SendMessage(permissionCheck);

            // Note that as of right now, the AllPro only receives 6 of the 11 responses.
            // So until that gets fixed, we could miss a 'refuse' response and try to switch
            // to 4X anyhow. That just results in an aborted read attempt, with no harm done.
            List<byte> result = new List<byte>();
            Message response = null;
            bool anyRefused = false;
            while ((response = await this.device.ReceiveMessage()) != null)
            {
                this.logger.AddDebugMessage("Parsing " + response.GetBytes().ToHex());
                Protocol.HighSpeedPermissionResult parsed = this.protocol.ParseHighSpeedPermissionResponse(response);
                if (!parsed.IsValid)
                {
                    await Task.Delay(100);
                    continue;
                }

                result.Add(parsed.DeviceId);

                if (parsed.PermissionGranted)
                {
                    this.logger.AddUserMessage(string.Format("Module 0x{0:X2} ({1}) has agreed to enter high-speed mode.", parsed.DeviceId, DeviceId.DeviceCategory(parsed.DeviceId)));

                    // Forcing a notification message should help ELM devices receive responses.
                    await notifier.ForceNotify();
                    await Task.Delay(100);
                    continue;
                }

                this.logger.AddUserMessage(string.Format("Module 0x{0:X2} ({1}) has refused to enter high-speed mode.", parsed.DeviceId, DeviceId.DeviceCategory(parsed.DeviceId)));
                anyRefused = true;
            }

            if (anyRefused)
            {
                return null;
            }

            return result;
        }

        /// <summary>
        /// Sends the provided message, with a retry loop. 
        /// </summary>
        public async Task<int> WritePayload(PcmInfo info, Message message, CancellationToken cancellationToken)
        {
            for (int retryCount = 0; retryCount < MaxSendAttempts; retryCount++)
            {
                await this.notifier.Notify();

                cancellationToken.ThrowIfCancellationRequested();

                await Task.Delay(50); // Allow the running kernel time to enter the ReadMessage function

                await device.SendMessage(message);

                if (await WaitForSuccess((msg) => protocol.ValidateUploadResponse(info, msg), cancellationToken))
                {
                    return retryCount;
                }

                this.logger.AddDebugMessage("WritePayload: Upload request failed.");

                await Task.Delay(100);
                await this.SendToolPresentNotification();
            }

            throw new ObdException($"WritePayload: Giving up. Attempted to write {MaxSendAttempts} time(s).", ObdExceptionReason.Error);
        }
    }
}
