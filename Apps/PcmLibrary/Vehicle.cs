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
        /// How many times we should attempt to send a message before giving up.
        /// </summary>
        public const int MaxSendAttempts = 10;

        /// <summary>
        /// How many times we should attempt to receive a message before giving up.
        /// </summary>
        /// <remarks>
        /// 10 is too small for the case when we get a bunch of "chatter 
        /// suppressed" messages right before trying to upload the kernel.
        /// Might be worth making this a parameter to the retry loops since
        /// in most cases when only need about 5.
        /// </remarks>
        public const int MaxReceiveAttempts = 5;

        /// <summary>
        /// The device we'll use to talk to the PCM.
        /// </summary>
        private Device device;

        /// <summary>
        /// This class knows how to generate message to send to the PCM.
        /// </summary>
        private Protocol protocol;
        
        /// <summary>
        /// This is how we send user-friendly status messages and developer-oriented debug messages to the UI.
        /// </summary>
        private ILogger logger;

        /// <summary>
        /// Use this to periodically send tool-present messages during long operations, to 
        /// discourage devices on the VPW bus from sending messages that could interfere
        /// with whatever the application is doing.
        /// </summary>
        private ToolPresentNotifier notifier;

        /// <summary>
        /// Gets a string that describes the device this instance is using.
        /// </summary>
        public string DeviceDescription
        {
            get
            {
                return this.device.ToString();
            }
        }

        public int DeviceMaxFlashWriteSendSize
        {
            get
            {
                return this.device.MaxFlashWriteSendSize;
            }
        }

        public int DeviceMaxReceiveSize
        {
            get
            {
                return this.device.MaxReceiveSize;
            }
        }

        public bool Supports4X
        {
            get => this.device.Supports4X;
        }

        public bool Enable4xReadWrite
        {
            set
            {
                this.device.Enable4xReadWrite = value;
            }

            get => this.device.Enable4xReadWrite;
        }

        public Int32 UserDefinedKey
        {
            get; set;
        } = -1;

        /// <summary>
        /// Silences Kernel ID reporting
        /// </summary>
        /// <remarks>
        /// See note Vehicle.Kernel PCMExecute(...)
        /// </remarks>
        public bool ReportKernelID
        {
            get; set;
        } = true;

        /// <summary>
        /// Constructor.
        /// </summary>
        public Vehicle(
            Device device, 
            Protocol protocol,
            ILogger logger,
            ToolPresentNotifier notifier)
        {
            this.device = device;
            this.protocol = protocol;
            this.logger = logger;
            this.notifier = notifier;
        }

        /// <summary>
        /// Finalizer.
        /// </summary>
        ~Vehicle()
        {
            this.Dispose(false);
        }

        /// <summary>
        /// Implements IDisposable.Dispose.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Part of the Dispose pattern.
        /// </summary>
        protected void Dispose(bool isDisposing)
        {
            if (this.device != null)
            {
                this.device.Dispose();
                this.device = null;
            }
        }

        /// <summary>
        /// Re-initialize the device.
        /// </summary>
        public async Task ResetConnection()
        {
            var task = this.device.Initialize();

            if (!await task.AwaitWithTimeout(TimeSpan.FromSeconds(10)))
            {
                throw new ObdException("Timed out waiting for initialize while resetting connection.", ObdExceptionReason.Timeout);
            }
        }

        /// <summary>
        /// Send a tool-present notfication.  (Or not, depending on how much 
        /// time has passed since the last notificationw was sent.)
        /// </summary>
        /// <returns></returns>
        public async Task SendToolPresentNotification()
        {
            if (!this.device.Supports4X && (this.device.MaxFlashWriteSendSize > 600 || this.device.MaxReceiveSize > 600))
            {
                await this.notifier.ForceNotify();
            }
            else
            {
                await this.notifier.Notify();
            }
        }

        /// <summary>
        /// Send a tool-present notfication.  (Or not, depending on how much 
        /// time has passed since the last notificationw was sent.)
        /// </summary>
        /// <returns></returns>
        public async Task ForceSendToolPresentNotification()
        {
            await this.notifier.ForceNotify();
        }

        /// <summary>
        /// Change the device's timeout.
        /// </summary>
        public async Task<TimeoutScenario> SetDeviceTimeout(TimeoutScenario scenario)
        {
            return await this.device.SetTimeout(scenario);
        }

        /// <summary>
        /// Clear the device's incoming-message queue.
        /// </summary>
        public void ClearDeviceMessageQueue()
        {
            this.device.ClearMessageQueue();
        }

        /// <summary>
        /// Query factory. One could argue that this is in the wrong place.
        /// </summary>
        public Query<T> CreateQuery<T>(
            Func<Message> generator, 
            Func<Message,T> parser, 
            CancellationToken cancellationToken)
        {
            return new Query<T>(
                this.device,
                generator,
                parser,
                this.logger,
                cancellationToken,
                this.notifier);
        }

        public async Task SendMessage(Message message)
        {
            await this.device.SendMessage(message);
        }

        public async Task<Message> ReceiveMessage()
        {
            return await this.device.ReceiveMessage();
        }

        /// <summary>
        /// Note that this has only been confirmed to work with ObdLink ScanTool devices.
        /// AllPro doesn't get the reply for some reason.
        /// Might work with AVT or J-tool, that hasn't been tested.
        /// </summary>
        public async Task<bool> IsInRecoveryMode()
        {
            this.device.ClearMessageQueue();

            for (int iterations = 0; iterations < 10; iterations++)
            {
                await this.SendMessage(new Message(new byte[] { Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, 0x62 }));

                Message response = await this.device.ReceiveMessage();

                if (response == null)
                {
                    continue;
                }

                if (this.protocol.ValidateRecoveryModeBroadcast(response))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Unlock the PCM by requesting a 'seed' and then sending the corresponding 'key' value.
        /// </summary>
        public async Task<bool> UnlockEcu(int keyAlgorithm)
        {
            await this.device.SetTimeout(TimeoutScenario.ReadProperty);

            this.device.ClearMessageQueue();

            this.logger.AddDebugMessage("Sending seed request.");
            Message seedRequest = this.protocol.CreateSeedRequest();

            await this.SendMessage(seedRequest);

            bool seedReceived = false;
            UInt16 seedValue = 0;

            for (int attempt = 1; attempt < MaxReceiveAttempts; attempt++)
            {
                Message seedResponse = await this.device.ReceiveMessage();
                if (seedResponse == null)
                {
                    this.logger.AddDebugMessage("No response to seed request.");
                    return false;
                }

                if (this.protocol.IsUnlocked(seedResponse.GetBytes()))
                {
                    this.logger.AddUserMessage("PCM is already unlocked");
                    return true;
                }

                this.logger.AddDebugMessage("Parsing seed value.");

                try
                {
                    UInt16 seedValueResponse = this.protocol.ParseSeed(seedResponse.GetBytes());

                    seedValue = seedValueResponse;
                    seedReceived = true;
                    break;
                }
                catch (ObdException ex)
                {
                    this.logger.AddDebugMessage($"Unable to parse seed response. Attempt #{attempt}, exception {ex.Message}");
                }
            }

            if (!seedReceived)
            {
                this.logger.AddUserMessage("No seed reponse received, unable to unlock PCM.");
                return false;
            }

            if (seedValue == 0x0000)
            {
                this.logger.AddUserMessage("PCM Unlock not required");
                return true;
            }

            UInt16 key;
            if (UserDefinedKey == -1)
            {
                key = KeyAlgorithm.GetKey(keyAlgorithm, seedValue);
            }
            else
            {
                this.logger.AddUserMessage($"User Defined Key: 0x{UserDefinedKey.ToString("X4")}");
                key = (UInt16)UserDefinedKey;
            }

            this.logger.AddDebugMessage("Sending unlock request (" + seedValue.ToString("X4") + ", " + key.ToString("X4") + ")");
            
            Message unlockRequest = this.protocol.CreateUnlockRequest(key);
            await this.SendMessage(unlockRequest);
            
            for (int attempt = 1; attempt < MaxReceiveAttempts; attempt++)
            {
                Message unlockResponse = await this.device.ReceiveMessage();
                if (unlockResponse == null)
                {
                    this.logger.AddDebugMessage("No response to unlock request. Attempt #" + attempt.ToString());
                    continue;
                }

                var result = this.protocol.ParseUnlockResponse(unlockResponse.GetBytes());
                if (result == null)
                {
                    return true;
                }

                this.logger.AddUserMessage(result);
            }

            this.logger.AddUserMessage("Unable to process unlock response.");
            return false;
        }

        /// <summary>
        /// Wait for an incoming message.
        /// </summary>
        private async Task<Message> ReceiveMessage(CancellationToken cancellationToken)
        {
            Message response = null;

            for (int pause = 0; pause < 3; pause++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                response = await this.device.ReceiveMessage();
                if (response == null)
                {
                    this.logger.AddDebugMessage("No response to read request yet.");
                    await Task.Delay(10);
                    continue;
                }

                break;
            }

            return response;
        }

        /// <summary>
        /// Read messages from the device, ignoring irrelevant messages.
        /// </summary>
        private async Task<bool> WaitForSuccess(Action<Message> validate, CancellationToken cancellationToken, int attempts = MaxReceiveAttempts)
        {
            for(int attempt = 1; attempt<=attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Message message = await this.device.ReceiveMessage();
                if(message == null)
                {
                    await this.SendToolPresentNotification();
                    continue;
                }

                try
                {
                    validate(message);
                }
                catch (ObdException ex)
                {
                    if (ex.Reason != ObdExceptionReason.Refused)
                    {
                        throw;
                    }

                    this.logger.AddDebugMessage($"Ignoring message: {message}");
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Send and receive a read-memory request.
        /// </summary>
        public async Task<byte[]> ReadMemory(
            Func<Message> messageFactory,
            Func<Message, byte[]> messageParser,
            CancellationToken cancellationToken)
        {
            Message message = messageFactory();

            await this.device.SendMessage(message);

            for (int receiveAttempt = 1; receiveAttempt <= MaxReceiveAttempts; receiveAttempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Message payloadMessage = await this.device.ReceiveMessage();
                if (payloadMessage == null)
                {
                    this.logger.AddDebugMessage("No payload following read request.");
                    continue;
                }

                this.logger.AddDebugMessage("Processing message");

                return messageParser(payloadMessage);
            }

            throw new ObdException("Reached maximum attempts waiting to read memory.", ObdExceptionReason.Timeout);
        }
    }
}
