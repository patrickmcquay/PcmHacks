﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace PcmHacking
{
    public enum BlockCopyType
    {
        // Copy to RAM or Flash
        Copy = 0x00,

        // Execute after copying to RAM
        Execute = 0x80,

        // Test copy to flash, but do not unlock or actually write.
        TestWrite = 0x44,
    };

    public partial class Protocol
    {
        /// <summary>
        /// Create a block message from the supplied arguments.
        /// </summary>
        public Message CreateBlockMessage(byte[] Payload, int Offset, int Length, int Address, BlockCopyType copyType)
        {
            byte[] Buffer = new byte[10 + Length + 2];
            byte[] Header = new byte[10];

            byte Size1 = unchecked((byte)(Length >> 8));
            byte Size2 = unchecked((byte)(Length & 0xFF));
            byte Addr1 = unchecked((byte)(Address >> 16));
            byte Addr2 = unchecked((byte)(Address >> 8));
            byte Addr3 = unchecked((byte)(Address & 0xFF));

            Header[0] = Priority.Block;
            Header[1] = DeviceId.Pcm;
            Header[2] = DeviceId.Tool;
            Header[3] = Mode.PCMUpload;
            Header[4] = (byte)copyType;
            Header[5] = Size1;
            Header[6] = Size2;
            Header[7] = Addr1;
            Header[8] = Addr2;
            Header[9] = Addr3;

            System.Buffer.BlockCopy(Header, 0, Buffer, 0, Header.Length);
            System.Buffer.BlockCopy(Payload, Offset, Buffer, Header.Length, Length);

            return new Message(VpwUtilities.AddBlockChecksum(Buffer));
        }

        /// <summary>
        /// Create a request to uploade size bytes to the given address
        /// </summary>
        /// <remarks>
        /// Note that mode 0x34 is only a request. The actual payload is sent as a mode 0x36.
        /// </remarks>
        public Message CreateUploadRequest(PcmInfo info, int Size)
        {
            switch (info.HardwareType)
            {
                case PcmType.P10:
                case PcmType.P12:
                    byte[] requestBytesP12 = { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.PCMUploadRequest };
                    return new Message(requestBytesP12);

                default:
                    byte[] requestBytes = { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.PCMUploadRequest, Submode.Null, 0x00, 0x00, 0x00, 0x00, 0x00 };
                    requestBytes[5] = unchecked((byte)(Size >> 8));
                    requestBytes[6] = unchecked((byte)(Size & 0xFF));
                    if (info.LoaderRequired)
                    {
                        requestBytes[7] = unchecked((byte)(info.LoaderBaseAddress >> 16));
                        requestBytes[8] = unchecked((byte)(info.LoaderBaseAddress >> 8));
                        requestBytes[9] = unchecked((byte)(info.LoaderBaseAddress & 0xFF));
                    }
                    else
                    {
                        requestBytes[7] = unchecked((byte)(info.KernelBaseAddress >> 16));
                        requestBytes[8] = unchecked((byte)(info.KernelBaseAddress >> 8));
                        requestBytes[9] = unchecked((byte)(info.KernelBaseAddress & 0xFF));
                    }
                    return new Message(requestBytes);
            }
        }

        /// <summary>
        /// Parse the response to a request for permission to upload a RAM kernel (or part of a kernel).
        /// </summary>
        public bool IsUploadPermissionResponseValid(PcmInfo info, Message message)
        {
            switch (info.HardwareType)
            {
                case PcmType.P10:
                case PcmType.P12:
                    return this.IsMessageValid(message, Priority.Physical0, Mode.PCMUploadRequest);

                default:
                    return this.IsMessageValid(message, Priority.Physical0, Mode.PCMUploadRequest);
            }

            // In case the PCM sends back a 7F message with an 8C priority byte...
            this.IsMessageValid(message, Priority.Physical0High, Mode.PCMUploadRequest);
        }

        /// <summary>
        /// Parse the response to an upload-to-RAM request.
        /// </summary>
        public bool ValidateUploadResponse(PcmInfo info, Message message)
        {
            switch (info.HardwareType)
            {
                case PcmType.P01_P59:
                    return this.IsMessageValid(message, Priority.Block, Mode.PCMUpload);

                case PcmType.P10:
                    return this.IsMessageValid(message, Priority.Block, Mode.PCMUpload);

                case PcmType.P12:
                    return this.IsMessageValid(message, Priority.Physical0, Mode.PCMUpload);

                default:
                    throw new ArgumentException($"ValidateUploadResponse unknown PcmType: {info.HardwareType}");
            }
        }

        /// <summary>
        /// Create a request to read an arbitrary address range.
        /// </summary>
        /// <remarks>
        /// This command is only understood by the reflash kernel.
        /// </remarks>
        /// <param name="startAddress">Address of the first byte to read.</param>
        /// <param name="length">Number of bytes to read.</param>
        /// <returns></returns>
        public Message CreateReadRequest(int startAddress, int length)
        {
            byte[] request = { Priority.Block, DeviceId.Pcm, DeviceId.Tool, 0x35, 0x01, (byte)(length >> 8), (byte)(length & 0xFF), (byte)(startAddress >> 16), (byte)((startAddress >> 8) & 0xFF), (byte)(startAddress & 0xFF) };
            byte[] request2 = { Priority.Block, DeviceId.Pcm, DeviceId.Tool, 0x37, 0x01, (byte)(length >> 8), (byte)(length & 0xFF), (byte)(startAddress >> 24), (byte)(startAddress >> 16), (byte)((startAddress >> 8) & 0xFF), (byte)(startAddress & 0xFF) };

            if (startAddress > 0xFFFFFF)
            {
                return new Message(request2);
            }
            else
            {
                return new Message(request);
            }
        }

        /// <summary>
        /// Parse the payload of a read request.
        /// </summary>
        /// <remarks>
        /// It is the callers responsability to check the ResponseStatus for errors
        /// </remarks>
        public byte[] ParsePayload(Message message, int length, int expectedAddress)
        {
            byte[] actual = message.GetBytes();
            byte[] expected = new byte[] { Priority.Block, DeviceId.Tool, DeviceId.Pcm, Mode.PCMUpload };
            if (!VerifyInitialBytes(actual, expected))
            {
                throw new ObdException($"Unexpected response, expected {expected}, got {actual}", ObdExceptionReason.UnexpectedResponse);
            }

            // Ensure that we can read the data length and start address from the message.
            if (actual.Length < 10)
            {
                throw new ObdException($"Truncated response, got {actual}", ObdExceptionReason.Truncated);
            }

            // Read the data length.
            int dataLength = (actual[5] << 8) + actual[6];

            // Read and validate the data start address.
            int actualAddress = ((actual[7] << 16) + (actual[8] << 8) + actual[9]);
            if (actualAddress != expectedAddress)
            {
                throw new ObdException($"Unexpected data start address, expected {expectedAddress}, got {actualAddress}", ObdExceptionReason.UnexpectedResponse);
            }

            byte[] result = new byte[dataLength];

            // Normal block
            if (actual[4] == 1)
            {
                // With normal encoding, data length should be actual length minus header size
                if (actual.Length - 12 < dataLength)
                {
                    throw new ObdException($"Truncated response, got {actual}", ObdExceptionReason.Truncated);
                }

                // Verify block checksum
                UInt16 ValidSum = VpwUtilities.CalcBlockChecksum(actual);
                int PayloadSum = (actual[dataLength + 10] << 8) + actual[dataLength + 11];
                Buffer.BlockCopy(actual, 10, result, 0, dataLength);
                if (PayloadSum != ValidSum)
                {
                    throw new ObdException($"Invalid checksum, expected {ValidSum} got {PayloadSum}", ObdExceptionReason.Error);
                }

                return result;
            }
            // RLE block
            else if (actual[4] == 2)
            {
                // This isnt going to work with existing kernels... need to support variable length.

                // PM -- what in the world? Rewrite the result with whatever is in actual[10], and then error with that as a result?
                //byte value = actual[10];

                //for (int index = 0; index < dataLength; index++)
                //{
                //    result[index] = value;
                //}

                throw new ObdException($"RLE block type doesnt work.", ObdExceptionReason.Error);
            }
            else
            {
                throw new ObdException($"Unknown block type {actual[4]}", ObdExceptionReason.Error);
            }
        }
    }
}
