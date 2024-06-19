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
    /// From the application's perspective, this class is the API to the vehicle.
    /// </summary>
    /// <remarks>
    /// Methods in this class are high-level operations like "get the VIN," or "read the contents of the EEPROM."
    /// </remarks>
    public partial class Vehicle : IDisposable
    {
        /// <summary>
        /// Query the PCM's VIN.
        /// </summary>
        public async Task<string> QueryVin()
        {
            await this.device.SetTimeout(TimeoutScenario.ReadProperty);

            this.device.ClearMessageQueue();

            await this.device.SendMessage(this.protocol.CreateVinRequest1());

            Message response1 = await this.device.ReceiveMessage();
            if (response1 == null)
            {
                throw new ObdException("Unknown. No response to request for block 1.", ObdExceptionReason.Timeout);
            }

            await this.device.SendMessage(this.protocol.CreateVinRequest2());

            Message response2 = await this.device.ReceiveMessage();
            if (response2 == null)
            {
                throw new ObdException("Unknown. No response to request for block 2.", ObdExceptionReason.Timeout);
            }

            await this.device.SendMessage(this.protocol.CreateVinRequest3());

            Message response3 = await this.device.ReceiveMessage();
            if (response3 == null)
            {
                throw new ObdException("Unknown. No response to request for block 3.", ObdExceptionReason.Timeout);
            }

            return this.protocol.ParseVinResponses(response1.GetBytes(), response2.GetBytes(), response3.GetBytes());
        }

        /// <summary>
        /// Query the PCM's Serial Number.
        /// </summary>
        public async Task<string> QuerySerial()
        {
            await this.device.SetTimeout(TimeoutScenario.ReadProperty);

            this.device.ClearMessageQueue();

            await this.device.SendMessage(this.protocol.CreateSerialRequest1());

            Message response1 = await this.device.ReceiveMessage();
            if (response1 == null)
            {
                throw new ObdException("Unknown. No response to request for block 1.", ObdExceptionReason.Timeout);
            }

            await this.device.SendMessage(this.protocol.CreateSerialRequest2());

            Message response2 = await this.device.ReceiveMessage();
            if (response2 == null)
            {
                throw new ObdException("Unknown. No response to request for block 2.", ObdExceptionReason.Timeout);
            }

            await this.device.SendMessage(this.protocol.CreateSerialRequest3());

            Message response3 = await this.device.ReceiveMessage();
            if (response3 == null)
            {
                throw new ObdException("Unknown. No response to request for block 3.", ObdExceptionReason.Timeout);
            }

            return this.protocol.ParseSerialResponses(response1, response2, response3);
        }

        /// <summary>
        /// Query the PCM's Broad Cast Code.
        /// </summary>
        public async Task<string> QueryBCC()
        {
            await this.device.SetTimeout(TimeoutScenario.ReadProperty);

            var query = this.CreateQuery(
                this.protocol.CreateBCCRequest,
                this.protocol.ParseBCCresponse, 
                CancellationToken.None);

            return await query.Execute();
        }

        /// <summary>
        /// Query the PCM's Manufacturer Enable Counter (MEC)
        /// </summary>
        public async Task<string> QueryMEC()
        {
            await this.device.SetTimeout(TimeoutScenario.ReadProperty);

            var query = this.CreateQuery(
                this.protocol.CreateMECRequest,
                this.protocol.ParseMECresponse,
                CancellationToken.None);

            return await query.Execute();
        }

        /// <summary>
        /// Query the PCM's voltage PID.
        /// </summary>
        public async Task<string> QueryVoltage()
        {
            await this.device.SetTimeout(TimeoutScenario.ReadProperty);

            var query = this.CreateQuery(
                ()=>this.protocol.CreatePidRequest(0x1141),
                this.protocol.ParsePidResponse,
                CancellationToken.None);

            int intResponse = await query.Execute();
            double voltage = intResponse / 10.0;

            return voltage.ToString();
        }

        /// <summary>
        /// Update the PCM's VIN
        /// </summary>
        /// <remarks>
        /// Requires that the PCM is already unlocked
        /// </remarks>
        public async Task UpdateVin(string vin)
        {
            this.device.ClearMessageQueue();

            if (vin.Length != 17) // should never happen, but....
            {
                throw new ObdException($"VIN {vin} is not 17 characters long!", ObdExceptionReason.Error);
            }

            this.logger.AddUserMessage("Changing VIN to " + vin);

            byte[] bvin = Encoding.ASCII.GetBytes(vin);
            byte[] vin1 = new byte[6] { 0x00, bvin[0], bvin[1], bvin[2], bvin[3], bvin[4] };
            byte[] vin2 = new byte[6] { bvin[5], bvin[6], bvin[7], bvin[8], bvin[9], bvin[10] };
            byte[] vin3 = new byte[6] { bvin[11], bvin[12], bvin[13], bvin[14], bvin[15], bvin[16] };

            this.logger.AddUserMessage("Block 1");
            await WriteBlock(BlockId.Vin1, vin1);

            this.logger.AddUserMessage("Block 2");
            await WriteBlock(BlockId.Vin2, vin2);

            this.logger.AddUserMessage("Block 3");
            await WriteBlock(BlockId.Vin3, vin3);
        }

        /// <summary>
        /// Query the PCM's operating system ID.
        /// </summary>
        /// <returns></returns>
        public async Task<UInt32> QueryOperatingSystemId(CancellationToken cancellationToken)
        {
            await this.device.SetTimeout(TimeoutScenario.ReadProperty);
            return await this.QueryUnsignedValue(this.protocol.CreateOperatingSystemIdReadRequest, cancellationToken);
        }

        /// <summary>
        /// Query the PCM's Hardware ID.
        /// </summary>
        /// <remarks>
        /// Note that this is a software variable and my not match the hardware at all of the software runs.
        /// </remarks>
        public async Task<UInt32> QueryHardwareId()
        {
            return await this.QueryUnsignedValue(this.protocol.CreateHardwareIdReadRequest, CancellationToken.None);
        }

        /// <summary>
        /// Query the PCM's calibration ID.
        /// </summary>
        public async Task<UInt32> QueryCalibrationId()
        {
            await this.device.SetTimeout(TimeoutScenario.ReadProperty);

            var query = this.CreateQuery(
                this.protocol.CreateCalibrationIdReadRequest,
                this.protocol.ParseUInt32FromBlockReadResponse,
                CancellationToken.None);
            return await query.Execute();
        }

        /// <summary>
        /// Helper function for queries that return unsigned 32-bit integers.
        /// </summary>
        private async Task<UInt32> QueryUnsignedValue(Func<Message> generator, CancellationToken cancellationToken)
        {
            await this.device.SetTimeout(TimeoutScenario.ReadProperty);

            var query = this.CreateQuery(generator, this.protocol.ParseUInt32FromBlockReadResponse, cancellationToken);
            return await query.Execute();
        }
    }
}
