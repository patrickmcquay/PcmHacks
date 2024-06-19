using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using J2534DotNet;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace PcmHacking
{
    /// <summary>
    /// This class encapsulates all code that is unique to the AVT 852 interface.
    /// </summary>
    ///
    class J2534Device : Device
    {
        /// <summary>
        /// Configuration settings
        /// </summary>
        public int ReadTimeout = 3000;
        public int WriteTimeout = 2000;

        /// <summary>
        /// variety of properties used to id channels, fitlers and status
        /// </summary>
        private J2534_Struct J2534Port;
        public List<ulong> Filters;
        private int DeviceID;
        private int ChannelID;
        private ProtocolID Protocol;
        public bool IsProtocolOpen;
        public bool IsJ2534Open;
        private const string PortName = "J2534";
        private const uint MessageFilter = 0x6CF010;
        public string ToolName = "";

        /// <summary>
        /// global error variable for reading/writing. (Could be done on the fly)
        /// TODO, keep record of all errors for debug
        /// </summary>
        public J2534Err OBDError;

        /// <summary>
        /// J2534 has two parts.
        /// J2534device which has the supported protocols ect as indicated by dll and registry.
        /// J2534extended which is al the actual commands and functions to be used. 
        /// </summary>
        struct J2534_Struct
        {
            public J2534 Functions;
            public J2534DotNet.J2534Device LoadedDevice;
        }

        public J2534Device(J2534DotNet.J2534Device jport, ILogger logger) : base(logger)
        {
            J2534Port = new J2534_Struct();
            J2534Port.Functions = new J2534();
            J2534Port.LoadedDevice = jport;

            // Reduced from 4096+12 for the MDI2
            this.MaxSendSize = 2048 + 12;    // J2534 Standard is 4KB
            this.MaxReceiveSize = 2048 + 12; // J2534 Standard is 4KB
            this.Supports4X = true;
            this.SupportsSingleDpidLogging = true;
            this.SupportsStreamLogging = true;
        }

        protected override void Dispose(bool disposing)
        {
            DisconnectTool();
        }

        public override string ToString()
        {
            return "J2534 Device";
        }

        // This needs to return Task<bool> for consistency with the Device base class.
        // However it doesn't do anything asynchronous, so to make the code more readable
        // it just wraps a private method that does the real work and returns a bool.
        public override Task Initialize()
        {
            this.InitializeInternal();

            return Task.FromResult(true);
        }

        // This returns 'bool' for the sake of readability. That bool needs to be
        // wrapped in a Task object for the public Initialize method.
        private void InitializeInternal()
        {
            Filters = new List<ulong>();

            this.Logger.AddUserMessage("Initializing " + this.ToString());

            J2534Err m; // hold returned messages for processing
            bool m2;
            double volts;

            // Check J2534 API
            //this.Logger.AddDebugMessage(J2534Port.Functions.ToString());

            // Check not already loaded
            if (IsLoaded == true)
            {
                // Disconnect protocol before disconnecting tool.
                DisconnectFromProtocol();
                this.Logger.AddDebugMessage("Successfully disconnected from protocol.");

                // Disconnect tool before unloading DLL.
                DisconnectTool();
                this.Logger.AddDebugMessage("Successfully disconnected from tool.");

                // Unload DLL.
                CloseLibrary();
                this.Logger.AddDebugMessage("Successfully unloaded DLL.");
            }

            // Connect to requested DLL
            LoadLibrary(J2534Port.LoadedDevice);
            this.Logger.AddUserMessage("Loaded DLL");

            // Connect to scantool
            ConnectTool();
            this.Logger.AddUserMessage("Connected to the device.");
            
            // Optional.. read API,firmware version ect here
            
            // Read voltage
            volts = ReadVoltage();
            this.Logger.AddUserMessage("Battery Voltage is: " + volts.ToString());
        
            // Set Protocol
            ConnectToProtocol(ProtocolID.J1850VPW, BaudRate.J1850VPW_10400, ConnectFlag.NONE);
            this.Logger.AddDebugMessage("Protocol Set");

            // Set filter
            SetFilter(0xFEFFFF, J2534Device.MessageFilter, 0, TxFlag.NONE, FilterType.PASS_FILTER);
            
            this.Logger.AddDebugMessage("Device initialization complete.");
        }

        /// <summary>
        /// Not yet implemented.
        /// </summary>
        public override Task<TimeoutScenario> SetTimeout(TimeoutScenario scenario)
        {
            return Task.FromResult(this.currentTimeoutScenario);
        }

        /// <summary>
        /// This will process incoming messages for up to 500ms looking for a message
        /// </summary>
        public async Task<Message> FindResponse(Message expected)
        {
            for (int iterations = 0; iterations < 5; iterations++)
            {
                Message response = await this.ReceiveMessage();

                if (Utility.CompareArraysPart(response.GetBytes(), expected.GetBytes()))
                {
                    return response;
                }
                
                await Task.Delay(100);
            }

            throw new ObdException($"Timed out waiting for message. Expected: {expected}", ObdExceptionReason.Timeout);
        }

        /// <summary>
        /// Read an network packet from the interface, and return a Response/Message
        /// </summary>
        protected override Task Receive()
        {
            //this.Logger.AddDebugMessage("Trace: Read Network Packet");

            int NumMessages = 1;
            //IntPtr rxMsgs = Marshal.AllocHGlobal((int)(Marshal.SizeOf(typeof(PassThruMsg)) * NumMessages));
            List<PassThruMsg> rxMsgs = new List<PassThruMsg>();
            PassThruMsg PassMess;
            OBDError = 0; // Clear any previous faults

            Stopwatch sw = new Stopwatch();
            sw.Start();

            do
            {
                NumMessages = 1;
                OBDError = J2534Port.Functions.ReadMsgs((int)ChannelID, ref rxMsgs, ref NumMessages, ReadTimeout);
                if (OBDError != J2534Err.STATUS_NOERROR)
                {
                    this.Logger.AddDebugMessage("ReadMsgs OBDError: " + OBDError);
                    return Task.FromResult(0);
                }

                PassMess = rxMsgs.Last();
                if ((int)PassMess.RxStatus == (((int)RxStatus.NONE) + ((int)RxStatus.TX_MSG_TYPE)) || (PassMess.RxStatus == RxStatus.START_OF_MESSAGE))
                {
                    continue;
                }
                else
                {
                    byte[] TempBytes = PassMess.Data;
                    // Perform additional filter check if required here... or show to debug
                    break; // Exit loop
                }
            } while (OBDError == J2534Err.STATUS_NOERROR || sw.ElapsedMilliseconds > (long)ReadTimeout);
            sw.Stop();


            if (OBDError != J2534Err.STATUS_NOERROR || sw.ElapsedMilliseconds > (long)ReadTimeout)
            {
                this.Logger.AddDebugMessage("ReadMsgs OBDError: " + OBDError);
                return Task.FromResult(0);
            }

            this.Logger.AddDebugMessage("RX: " + PassMess.Data.ToHex());
            this.Enqueue(new Message(PassMess.Data, (ulong)PassMess.Timestamp, (ulong)OBDError));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Convert a Message to an J2534 formatted transmit, and send to the interface
        /// </summary>
        private void SendNetworkMessage(Message message, TxFlag Flags)
        {
            PassThruMsg TempMsg = new PassThruMsg(Protocol, Flags, message.GetBytes());

            int NumMsgs = 1;

            OBDError = J2534Port.Functions.WriteMsgs((int)ChannelID, ref TempMsg, ref NumMsgs, WriteTimeout);
            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                throw new ObdException(OBDError.ToString(), ObdExceptionReason.Error);
            }
        }
        
        /// <summary>
        /// Send a message, wait for a response, return the response.
        /// </summary>
        public override Task SendMessage(Message message)
        {
            this.Logger.AddDebugMessage("TX: " + message.GetBytes().ToHex());
            
            SendNetworkMessage(message, TxFlag.NONE);

            return Task.FromResult(true);
        }
        
        /// <summary>
        /// Load in dll
        /// </summary>
        private void LoadLibrary(J2534DotNet.J2534Device TempDevice)
        {
            ToolName = TempDevice.Name;
            J2534Port.LoadedDevice = TempDevice;

            if (!J2534Port.Functions.LoadLibrary(J2534Port.LoadedDevice))
            {
                throw new ObdException("Unable to load J2534 library", ObdExceptionReason.Error);
            }
        }

        /// <summary>
        /// Unload dll
        /// </summary>
        private void CloseLibrary()
        {
            if (!J2534Port.Functions.FreeLibrary())
            {
                throw new ObdException("Unable to free J2534 Library", ObdExceptionReason.Error);
            }
        }

        /// <summary>
        /// Connects to physical scantool
        /// </summary>
        private void ConnectTool()
        {
            DeviceID = 0;
            ChannelID = 0;
            Filters.Clear();
            OBDError = 0;
            OBDError = J2534Port.Functions.Open(ref DeviceID);

            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                throw new ObdException(OBDError.ToString(), ObdExceptionReason.Error);
            }
            
            IsJ2534Open = true;
        }

        /// <summary>
        /// Disconnects from physical scantool
        /// </summary>
        private void DisconnectTool()
        {
            OBDError = J2534Port.Functions.Close((int)DeviceID);
            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                throw new ObdException(OBDError.ToString(), ObdExceptionReason.Error);
            }

            IsJ2534Open = false;
        }

        /// <summary>
        /// Keep record if DLL has been loaded
        /// </summary>
        public bool IsLoaded
        {
            get 
            {
                try
                {

                    Process proc = Process.GetCurrentProcess();
                    foreach (ProcessModule dll in proc.Modules)
                    {
                        if (dll.FileName == J2534Port.LoadedDevice.FunctionLibrary)
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.Logger.AddDebugMessage(ex.Message);

                }
                return false;
            }
        }

        /// <summary>
        /// Connect to selected protocol
        /// Must provide protocol, speed, connection flags, recommended optional is pins
        /// </summary>
        private void ConnectToProtocol(ProtocolID ReqProtocol, BaudRate Speed, ConnectFlag ConnectFlags)
        {
            OBDError = J2534Port.Functions.Connect(DeviceID, ReqProtocol,  ConnectFlags,  Speed, ref ChannelID);
            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                throw new ObdException(OBDError.ToString(), ObdExceptionReason.Error);
            }

            Protocol = ReqProtocol;
            IsProtocolOpen = true;
        }

        /// <summary>
        /// Disconnect from protocol
        /// </summary>
        private void DisconnectFromProtocol()
        {
            OBDError = J2534Port.Functions.Disconnect((int)ChannelID);
            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                throw new ObdException(OBDError.ToString(), ObdExceptionReason.Error);
            }

            IsProtocolOpen = false;
        }

        /// <summary>
        /// Read battery voltage
        /// </summary>
        public double ReadVoltage()
        {
            int VoltsAsInt = 0;

            OBDError = J2534Port.Functions.ReadBatteryVoltage((int)DeviceID, ref VoltsAsInt);

            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                throw new ObdException(OBDError.ToString(), ObdExceptionReason.Error);
            }
            else
            {
                return VoltsAsInt / 1000.0;
            }
        }

        /// <summary>
        /// Set filter
        /// </summary>
        private void SetFilter(UInt32 Mask,UInt32 Pattern,UInt32 FlowControl,TxFlag txflag,FilterType Filtertype)
        {
            PassThruMsg maskMsg = new PassThruMsg(Protocol, txflag, new Byte[] { (byte)(0xFF & (Mask >> 16)), (byte)(0xFF & (Mask >> 8)), (byte)(0xFF & Mask) });
            PassThruMsg patternMsg = new PassThruMsg(Protocol, txflag, new Byte[] { (byte)(0xFF & (Pattern >> 16)), (byte)(0xFF & (Pattern >> 8)), (byte)(0xFF & Pattern) });
            int tempfilter = 0;
            OBDError = J2534Port.Functions.StartMsgFilter(ChannelID, Filtertype, ref maskMsg,ref patternMsg, ref tempfilter);

            if (OBDError != J2534Err.STATUS_NOERROR)
            {
                throw new ObdException(OBDError.ToString(), ObdExceptionReason.Error);
            }

            Filters.Add((ulong)tempfilter);
        }

        /// <summary>
        /// Set the interface to low (false) or high (true) speed
        /// </summary>
        /// <remarks>
        /// The caller must also tell the PCM to switch speeds
        /// </remarks>
        protected override Task SetVpwSpeedInternal(VpwSpeed newSpeed)
        {
            if (newSpeed == VpwSpeed.Standard)
            {
                this.Logger.AddDebugMessage("J2534 setting VPW 1X");
                // Disconnect from current protocol
                DisconnectFromProtocol();

                // Connect at new speed
                ConnectToProtocol(ProtocolID.J1850VPW, BaudRate.J1850VPW_10400, ConnectFlag.NONE);

                // Set Filter
                SetFilter(0xFEFFFF, J2534Device.MessageFilter, 0, TxFlag.NONE, FilterType.PASS_FILTER);
            }
            else
            {
                this.Logger.AddDebugMessage("J2534 setting VPW 4X");
                // Disconnect from current protocol
                DisconnectFromProtocol();

                // Connect at new speed
                ConnectToProtocol(ProtocolID.J1850VPW, BaudRate.J1850VPW_41600, ConnectFlag.NONE);

                // Set Filter
                SetFilter(0xFEFFFF, J2534Device.MessageFilter, 0, TxFlag.NONE, FilterType.PASS_FILTER);

            }

            return Task.FromResult(true);
        }

        public override void ClearMessageBuffer()
        {
            J2534Port.Functions.ClearRxBuffer((int)DeviceID);
            J2534Port.Functions.ClearTxBuffer((int)DeviceID);
        }
    }
}
