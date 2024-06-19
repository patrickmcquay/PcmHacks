using System;
using System.Collections.Generic;
using System.Text;

namespace PcmHacking
{
    public partial class Protocol
    {
        class Security
        {
            public const byte Denied  = 0x33; // Security Access Denied
            public const byte Allowed = 0x34; // Security Access Allowed
            public const byte Invalid = 0x35; // Invalid Key
            public const byte TooMany = 0x36; // Exceed Number of Attempts
            public const byte Delay  = 0x37; // Required Time Delay Not Expired
        }


        /// <summary>
        /// Create a request to retrieve a 'seed' value from the PCM
        /// </summary>
        public Message CreateSeedRequest()
        {
            byte[] Bytes = new byte[] { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.Seed, Submode.GetSeed };
            return new Message(Bytes);
        }


        /// <summary>
        /// Parse the response to a seed request.
        /// </summary>
        public UInt16 ParseSeed(byte[] response)
        {
            byte[] unlocked = { Priority.Physical0, 0x70, DeviceId.Pcm, Mode.Seed + Mode.Response, 0x01, 0x37 };
            byte[] seed = new byte[] { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.Seed + Mode.Response, 0x01 };

            //check if the seed doesnt match
            if (!VerifyInitialBytes(response, seed))
            {
                throw new ObdException($"Seed verification failed, expected {seed}, got {response}", ObdExceptionReason.Error);
            }

            //check if the PCM is already unlocked
            if (VerifyInitialBytes(response, unlocked))
            {
                return 0;
            }

            // Let's not reverse endianess
            return (UInt16)((response[5] << 8) | response[6]);
        }

        /// <summary>
        /// Create a request to send a 'key' value to the PCM
        /// </summary>
        public Message CreateUnlockRequest(UInt16 Key)
        {
            byte KeyHigh = (byte)((Key & 0xFF00) >> 8);
            byte KeyLow = (byte)(Key & 0xFF);
            byte[] Bytes = new byte[] { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.Seed, Submode.SendKey, KeyHigh, KeyLow };
            return new Message(Bytes);
        }

        /// <summary>
        /// Determine whether we were able to unlock the PCM.
        /// </summary>
        public string ParseUnlockResponse(byte[] unlockResponse)
        {
            if (unlockResponse.Length < 6)
            {
                return $"Unlock response truncated, expected 6 bytes, got {unlockResponse.Length} bytes.";
            }

            byte unlockCode = unlockResponse[5];

            switch (unlockCode)
            {
                case Security.Allowed:
                    return null;

                case Security.Denied:
                    return $"The PCM refused to unlock";

                case Security.Invalid:
                    return  $"The PCM didn't accept the unlock key value";
                    
                case Security.TooMany:
                    return $"The PCM did not accept the key - too many attempts";
                    
                case Security.Delay:
                    return $"The PCM is enforcing timeout lock";
                    
                default:
                    return $"Unknown unlock response code: 0x{unlockCode:X2}";
            }
        }

        /// <summary>
        /// Indicates whether or not the reponse indicates that the PCM is unlocked.
        /// </summary>
        public bool IsUnlocked(byte[] response)
        {
            byte[] unlocked = { Priority.Physical0, DeviceId.Tool, DeviceId.Pcm, Mode.Seed + Mode.Response, 0x01, 0x37 };
            if (VerifyInitialBytes(response, unlocked))
            {
                // To short to be a seed?
                if (response.Length < 7)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
