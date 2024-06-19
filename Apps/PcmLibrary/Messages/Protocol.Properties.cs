using System;
using System.Collections.Generic;
using System.Text;

namespace PcmHacking
{
    public partial class Protocol
    {
        /// <summary>
        /// Create a request to read the given block of PCM memory.
        /// </summary>
        public Message CreateReadRequest(byte Block)
        {
            byte[] Bytes = new byte[] { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.ReadBlock, Block };
            return new Message(Bytes);
        }

        /// <summary>
        /// Create a request to read the PCM's operating system ID.
        /// </summary>
        /// <returns></returns>
        public Message CreateOperatingSystemIdReadRequest()
        {
            return CreateReadRequest(BlockId.OperatingSystemID);
        }

        /// <summary>
        /// Create a request to read the PCM's Calibration ID.
        /// </summary>
        /// <returns></returns>
        public Message CreateCalibrationIdReadRequest()
        {
            return CreateReadRequest(BlockId.CalibrationID);
        }

        /// <summary>
        /// Create a request to read the PCM's Hardware ID.
        /// </summary>
        /// <returns></returns>
        public Message CreateHardwareIdReadRequest()
        {
            return CreateReadRequest(BlockId.HardwareID);
        }

        /// <summary>
        /// Parse the response to a block-read request.
        /// </summary>
        public UInt32 ParseUInt32FromBlockReadResponse(Message message)
        {
            return ParseUInt32WithoutSubMode(message, Mode.ReadBlock + Mode.Response);
        }

        #region VIN

        /// <summary>
        /// Create a request to read the first segment of the PCM's VIN.
        /// </summary>
        public Message CreateVinRequest1()
        {
            return CreateReadRequest(BlockId.Vin1);
        }

        /// <summary>
        /// Create a request to read the second segment of the PCM's VIN.
        /// </summary>
        public Message CreateVinRequest2()
        {
            return CreateReadRequest(BlockId.Vin2);
        }

        /// <summary>
        /// Create a request to read the thid segment of the PCM's VIN.
        /// </summary>
        public Message CreateVinRequest3()
        {
            return CreateReadRequest(BlockId.Vin3);
        }

        /// <summary>
        /// Parse the responses to the three requests for VIN information.
        /// </summary>
        public string ParseVinResponses(byte[] response1, byte[] response2, byte[] response3)
        {
            byte[] expected = new byte[] { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.ReadBlock + Mode.Response, BlockId.Vin1 };
            if (!VerifyInitialBytes(response1, expected))
            {
                throw new ObdException($"Error verifying Vin Block 1. Response: {response1}", ObdExceptionReason.Error);
            }

            expected = new byte[] { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.ReadBlock + Mode.Response, BlockId.Vin2 };
            if (!VerifyInitialBytes(response2, expected))
            {
                throw new ObdException($"Error verifying Vin Block 2. Response: {response2}", ObdExceptionReason.Error);
            }

            expected = new byte[] { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.ReadBlock + Mode.Response, BlockId.Vin3 };
            if (!VerifyInitialBytes(response3, expected))
            {
                throw new ObdException($"Error verifying Vin Block 3. Response: {response3}", ObdExceptionReason.Error);
            }

            byte[] vinBytes = new byte[17];

            Buffer.BlockCopy(response1, 6, vinBytes, 0, 5);
            Buffer.BlockCopy(response2, 5, vinBytes, 5, 6);
            Buffer.BlockCopy(response3, 5, vinBytes, 11, 6);

            return Encoding.ASCII.GetString(vinBytes);
        }

        #endregion

        #region Serial

        /// <summary>
        /// Create a request to read the first segment of the PCM's Serial Number.
        /// </summary>
        public Message CreateSerialRequest1()
        {
            return CreateReadRequest(BlockId.Serial1);
        }

        /// <summary>
        /// Create a request to read the second segment of the PCM's Serial Number.
        /// </summary>
        public Message CreateSerialRequest2()
        {
            return CreateReadRequest(BlockId.Serial2);
        }

        /// <summary>
        /// Create a request to read the thid segment of the PCM's Serial Number.
        /// </summary>
        public Message CreateSerialRequest3()
        {
            return CreateReadRequest(BlockId.Serial3);
        }

        /// <summary>
        /// Parse the responses to the three requests for Serial Number information.
        /// </summary>
        public string ParseSerialResponses(Message response1, Message response2, Message response3)
        { 
            byte[] expected = new byte[] { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.ReadBlock + Mode.Response, BlockId.Serial1 };
            if (!VerifyInitialBytes(response1, expected))
            {
                throw new ObdException($"Error verifying Serial Block 1. Response: {response1}", ObdExceptionReason.Error);
            }

            expected = new byte[] { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.ReadBlock + Mode.Response, BlockId.Serial2 };
            if (!VerifyInitialBytes(response2, expected))
            {
                throw new ObdException($"Error verifying Serial Block 2. Response: {response2}", ObdExceptionReason.Error);
            }

            expected = new byte[] { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.ReadBlock + Mode.Response, BlockId.Serial3 };
            if (!VerifyInitialBytes(response3, expected))
            {
                throw new ObdException($"Error verifying Serial Block 3. Response: {response3}", ObdExceptionReason.Error);
            }

            byte[] serialBytes = new byte[12];

            Buffer.BlockCopy(response1.GetBytes(), 5, serialBytes, 0, 4);
            Buffer.BlockCopy(response2.GetBytes(), 5, serialBytes, 4, 4);
            Buffer.BlockCopy(response3.GetBytes(), 5, serialBytes, 8, 4);

            byte[] printableBytes = Utility.GetPrintable(serialBytes);

            return Encoding.ASCII.GetString(printableBytes);
        }

        #endregion

        #region BCC

        /// <summary>
        /// Create a request to read the Broad Cast Code (BCC).
        /// </summary>
        public Message CreateBCCRequest()
        {
            return CreateReadRequest(BlockId.BCC);
        }

        public string ParseBCCresponse(Message responseMessage)
        {
            byte[] response = responseMessage.GetBytes();

            byte[] expected = new byte[] { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.ReadBlock + Mode.Response, BlockId.BCC };
            if (!VerifyInitialBytes(response, expected))
            {
                throw new ObdException($"Error verifying BCC response. Response: {response}", ObdExceptionReason.Error);
            }

            byte[] BCCBytes = new byte[4];
            Buffer.BlockCopy(response, 5, BCCBytes, 0, 4);

            byte[] printableBytes = Utility.GetPrintable(BCCBytes);

            return Encoding.ASCII.GetString(printableBytes);
        }

        #endregion

        #region MEC

        /// <summary>
        /// Create a request to read the Broad Cast Code (MEC).
        /// </summary>
        public Message CreateMECRequest()
        {
            return CreateReadRequest(BlockId.MEC);
        }

        public string ParseMECresponse(Message responseMessage)
        {
            byte[] response = responseMessage.GetBytes();

            byte[] expected = new byte[] { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.ReadBlock + Mode.Response, BlockId.MEC };
            if (!VerifyInitialBytes(response, expected))
            {
                throw new ObdException($"Error verifying MEC response. Response: {response}", ObdExceptionReason.Error);
            }

            return response[5].ToString();
        }

        #endregion
    }
}
