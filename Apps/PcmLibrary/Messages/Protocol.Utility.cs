using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PcmHacking
{
    public partial class Protocol
    {
        /// <summary>
        /// Parse a one-byte payload
        /// </summary>
        internal byte ParseByte(Message responseMessage, byte mode, byte submode)
        {
            byte[] expected = { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, (byte)(mode | Mode.Response), submode };
            if (!VerifyInitialBytes(responseMessage, expected))
            {
                byte[] refused = { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.NegativeResponse, mode, submode };
                if (VerifyInitialBytes(responseMessage, refused))
                {
                    throw new ObdException("PCM Refused", ObdExceptionReason.Refused);
                }

                throw new ObdException($"ParseByte verification failure: expected {expected} got {responseMessage.GetBytes()}", ObdExceptionReason.Error);
            }

            byte[] responseBytes = responseMessage.GetBytes();
            if (responseBytes.Length < 6)
            {
                throw new ObdException($"Response truncated, got {responseBytes}", ObdExceptionReason.Truncated);
            }

            return responseBytes[5];
        }

        /// <summary>
        /// Turn four bytes of payload into a UInt32.
        /// </summary>
        internal UInt32 ParseUInt32WithSubMode(Message responseMessage, byte mode, byte submode)
        {
            byte[] responseBytes = responseMessage.GetBytes();

            byte[] expected = { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, (byte)(mode | Mode.Response), submode };
            if (!VerifyInitialBytes(responseBytes, expected))
            {
                byte[] refused = { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.NegativeResponse, mode, submode };
                if (VerifyInitialBytes(responseBytes, refused))
                {
                    throw new ObdException("PCM Refused", ObdExceptionReason.Refused);
                }

                throw new ObdException($"ParseUInt32WithSubMode verification failure: expected {expected} got {responseBytes}", ObdExceptionReason.Error);
            }

            if (responseBytes.Length < 9)
            {
                throw new ObdException($"Response truncated, got {responseBytes}", ObdExceptionReason.Truncated);
            }

            int value =
                (responseBytes[5] << 24) |
                (responseBytes[6] << 16) |
                (responseBytes[7] << 8) |
                responseBytes[8];

            return (UInt32)value;
        }

        /// <summary>
        /// Parse a 32-bit value from the first four bytes of a message payload.
        /// </summary>
        public UInt32 ParseUInt32WithoutSubMode(Message message, byte responseMode)
        {
            byte[] responseBytes = message.GetBytes();
            byte[] expected = new byte[] { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, responseMode };

            if (!VerifyInitialBytes(responseBytes, expected))
            {
                throw new ObdException($"ParseUInt32WithoutSubMode verification failure: expected {expected} got {responseBytes}", ObdExceptionReason.Error);
            }

            if (responseBytes.Length < 9)
            {
                throw new ObdException($"Response truncated, got {responseBytes}", ObdExceptionReason.Truncated);
            }

            int value =
                (responseBytes[5] << 24) |
                (responseBytes[6] << 16) |
                (responseBytes[7] << 8) |
                responseBytes[8];

            return (UInt32)value;
        }

        /// <summary>
        /// Check for an accept/reject message with the given mode byte.
        /// </summary>
        /// <remarks>
        /// TODO: Make this private, use public methods that are tied to a specific message type.
        /// </remarks>
        public bool IsMessageValid(Message message, byte priority, byte mode, params byte[] data)
        {
            byte[] actual = message.GetBytes();

            byte[] failure = new byte[] { priority, DeviceId.Tool, DeviceId.Pcm, Mode.NegativeResponse, mode };
            if (VerifyInitialBytes(actual, failure))
            {
                return false;
            }

            byte[] success = new byte[] { priority, DeviceId.Tool, DeviceId.Pcm, (byte)(mode + Mode.Response), };
            if (VerifyInitialBytes(actual, success))
            {
                if (data != null && data.Length > 0)
                {
                    for (int index = 0; index < data.Length; index++)
                    {
                        const int headBytes = 4;
                        int actualLength = actual.Length;
                        int expectedLength = data.Length + headBytes;

                        if (actualLength < expectedLength)
                        {
                            return false;
                        }

                        if (actual[headBytes + index] != data[index])
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            throw new ObdException($"Unexpected response, the response neither succeeded, nor failed. Content: {actual}", ObdExceptionReason.UnexpectedResponse);
        }

        /// <summary>
        /// Confirm that the first portion of the 'actual' array of bytes matches the 'expected' array of bytes.
        /// </summary>
        private bool VerifyInitialBytes(Message actual, byte[] expected)
        {
            return VerifyInitialBytes(actual.GetBytes(), expected);
        }

        /// <summary>
        /// Confirm that the first portion of the 'actual' array of bytes matches the 'expected' array of bytes.
        /// </summary>
        private bool VerifyInitialBytes(byte[] actual, byte[] expected)
        {
            return actual.SequenceEqual(expected);
        }
    }
}
