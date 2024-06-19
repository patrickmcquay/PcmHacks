using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PcmHacking
{
    public class LogStartFailedException : Exception
    {
        public LogStartFailedException() : base("Unable to start logging.")
        { }

        public LogStartFailedException(string message) : base(message)
        { }
    }

    /// <summary>
    /// From the application's perspective, this class is the API to the vehicle.
    /// </summary>
    public partial class Vehicle : IDisposable
    {
        /// <summary>
        /// Create a logger.
        /// </summary>
        /// <remarks>
        /// The Logger implementation will vary depending on the device capability.
        /// </remarks>
        public Logger CreateLogger(
            uint osid,
            CanLogger canLogger,
            IEnumerable<LogColumn> columns,
            ILogger uiLogger)
        {
            return Logger.Create(
                this, 
                osid, 
                columns, 
                this.device.SupportsSingleDpidLogging,
                this.device.SupportsStreamLogging,
                canLogger,
                uiLogger);
        }

        /// <summary>
        /// Prepare the PCM to begin sending collections of parameters.
        /// </summary>
        public async Task<DpidCollection> ConfigureDpids(DpidConfiguration dpidConfiguration, uint osid)
        {
            List<byte> dpids = new List<byte>();

            await this.SetDeviceTimeout(TimeoutScenario.ReadProperty);

            foreach (ParameterGroup group in dpidConfiguration.ParameterGroups)
            {
                int position = 1;
                foreach (LogColumn column in group.LogColumns)
                {
                    PidParameter pidParameter = column.Parameter as PidParameter;
                    RamParameter ramParameter = column.Parameter as RamParameter;
                    int byteCount;

                    if (pidParameter != null)
                    {
                        Message configurationMessage = this.protocol.ConfigureDynamicData(
                            (byte)group.Dpid,
                            DefineBy.Pid,
                            position,
                            pidParameter.ByteCount,
                            pidParameter.PID);

                        // Response parsing happens further below.
                        await this.SendMessage(configurationMessage);

                        byteCount = pidParameter.ByteCount;
                    }
                    else if (ramParameter != null)
                    {
                        uint address;
                        if (ramParameter.TryGetAddress(osid, out address))
                        {
                            Message configurationMessage = this.protocol.ConfigureDynamicData(
                                (byte)group.Dpid,
                                DefineBy.Address,
                                position,
                                ramParameter.ByteCount,
                                address);

                            // Response parsing happens further below.
                            await this.SendMessage(configurationMessage);

                            byteCount = ramParameter.ByteCount;
                        }
                        else
                        {
                            this.logger.AddUserMessage(
                                string.Format("Parameter {0} is not defined for PCM {1}",
                                ramParameter.Name,
                                osid));
                            byteCount = 0;
                        }
                    }
                    else
                    {
                        throw new LogStartFailedException(
                            $"Why does this ParameterGroup contain a {column.Parameter.GetType().Name}? See {column.Parameter.Name}.");
                    }

                    // Wait for a success or fail message.
                    // TODO: move this into the protocol layer.
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        Message responseMessage = await this.ReceiveMessage();

                        if (responseMessage == null)
                        {
                            continue;
                        }

                        if (responseMessage.Length < 5)
                        {
                            continue;
                        }

                        if (responseMessage[3] == 0x6C)
                        {
                            this.logger.AddDebugMessage("Configured " + column.ToString());
                            break;
                        }

                        if (responseMessage[3] == 0x7F && responseMessage[4] == 0x2C)
                        {
                            this.logger.AddUserMessage("Unable to configure " + column.ToString());
                            throw new ParameterNotSupportedException(column.Parameter);
                        }
                    }


                    position += byteCount;
                }
                dpids.Add((byte)group.Dpid);
            }

            return new DpidCollection(dpids.ToArray());
        }

        /// <summary>
        /// Begin data logging.
        /// </summary>
        /// <remarks>
        /// In the future we could make "bool streaming" into an enum, with
        /// Fast, Slow, and  Mixed options. Mixed mode would request some 
        /// parameters at 10hz and others at 5hz.
        /// 
        /// This would require the user to specify, or the app to just know,
        /// which parameters to poll at 5hz rather than 10hz. A list of
        /// 5hz-friendly parameters is not out of the question. Some day.
        /// 
        /// The PCM always sends an error response to these messages, so
        /// we just ignore responses in all cases.
        /// </remarks>
        public async Task<bool> RequestDpids(DpidCollection dpids, bool streaming)
        {
            if (streaming)
            {
                // Request all of the parameters at 5hz using stream 1.
                Message step1 = this.protocol.RequestDpids(dpids, Protocol.DpidRequestType.Stream1);
                await this.SendMessage(step1);

                // Request all of the parameters at 5hz using stream 2. Now we get them all at 10hz.
                Message step2 = this.protocol.RequestDpids(dpids, Protocol.DpidRequestType.Stream2);
                await this.SendMessage(step2);
            }
            else
            {
                // Request one row of data.
                Message startMessage = this.protocol.RequestDpids(dpids, Protocol.DpidRequestType.SingleRow);
                await this.SendMessage(startMessage);
            }

            return true;
        }

        /// <summary>
        /// Read a dpid response from the PCM.
        /// </summary>
        public async Task<RawLogData> ReadLogData()
        {
            Message message;
            RawLogData result = null;

            for (int attempt = 1; attempt < 5; attempt++)
            {
                message = await this.ReceiveMessage();
                if (message == null)
                {
                    break;
                }
                
                if (this.protocol.TryParseRawLogData(message, out result))
                {
                    break;
                }
            } 

            return result;
        }

        /// <summary>
        /// This is needed to keep streaming logging active.
        /// </summary>
        public async Task SendDataLoggerPresentNotification()
        {
            Message message = this.protocol.CreateDataLoggerPresentNotification();
            await this.device.SendMessage(message);
        }

        /// <summary>
        /// Currently only used by VpwExplorer for testing.
        /// </summary>
        /// <param name="pid"></param>
        /// <returns></returns>
        public async Task<int> GetPid(UInt32 pid)
        {
            Message request = this.protocol.CreatePidRequest(pid);
            await this.SendMessage(request);

            Message responseMessage = await this.ReceiveMessage();
            if (responseMessage == null)
            {
                throw new ObdException("Error recieving PID response.", ObdExceptionReason.Error);
            }

            return this.protocol.ParsePidResponse(responseMessage);
        }

        public async Task<uint> GetRam(int address)
        {
            Query<uint> query = new Query<uint>(
                this.device,
                () => this.protocol.CreateRamRequest(address),
                this.protocol.ParseRamResponse,
                this.logger,
                CancellationToken.None);

            return await query.Execute();
        }
    }
}
