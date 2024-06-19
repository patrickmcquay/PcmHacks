using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PcmHacking
{
    /// <summary>
    /// Encapsulates the code to send a message and monitor the VPW bus until a response to that message is received.
    /// </summary>
    /// <remarks>
    /// The VPW protocol allows modules to inject messages whenever they want, so
    /// unexpected messages are common. After sending a message you can't assume
    /// that the next message on the bus will be the response that you were hoping
    /// to receive.
    /// </remarks>
    public class Query<T>
    {
        /// <summary>
        /// The device to use to send the message.
        /// </summary>
        private Device device;

        /// <summary>
        /// The code that will generate the outgoing message.
        /// </summary>
        private Func<Message> generator;

        /// <summary>
        /// Code that will select the response from whatever VPW messages appear on the bus.
        /// </summary>
        private Func<Message, T> filter;
                
        /// <summary>
        /// This will indicate when the user has requested cancellation.
        /// </summary>
        private CancellationToken cancellationToken;

        /// <summary>
        /// Optionally use tool-present messages as a way of polling for slow responses.
        /// </summary>
        private ToolPresentNotifier notifier;

        /// <summary>
        /// Provides access to the Results and Debug panes.
        /// </summary>
        private ILogger logger;

        public int MaxTimeouts { get; set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        public Query(Device device, Func<Message> generator, Func<Message, T> filter, ILogger logger, CancellationToken cancellationToken, ToolPresentNotifier notifier = null)
        {
            this.device = device;
            this.generator = generator;
            this.filter = filter;
            this.logger = logger;
            this.notifier = notifier;
            this.cancellationToken = cancellationToken;
            this.MaxTimeouts = 5;
        }

        /// <summary>
        /// Send the message, wait for the response.
        /// </summary>
        public async Task<T> Execute()
        {
            this.device.ClearMessageQueue();

            Message request = this.generator();

            for (int sendAttempt = 0; sendAttempt < 2; sendAttempt++)
            {
                this.cancellationToken.ThrowIfCancellationRequested();

                await this.device.SendMessage(request);

                // We'll read up to 50 times from the queue (just to avoid 
                // looping forever) but we will but only allow two timeouts.
                int timeouts = 0;
                for (int receiveAttempt = 0; receiveAttempt < 50; receiveAttempt++)
                {
                    this.cancellationToken.ThrowIfCancellationRequested();

                    Message received = await this.device.ReceiveMessage();

                    if (received == null)
                    {
                        timeouts++;
                        if (timeouts >= this.MaxTimeouts)
                        {
                            // Maybe try sending again if we haven't run out of send attempts.
                            this.logger.AddDebugMessage($"Receive timed out. Attempt #{receiveAttempt}, Timeout #{timeouts}.");
                            break;
                        }

                        if (this.notifier != null)
                        {
                            await this.notifier.ForceNotify();
                        }

                        continue;
                    }

                    try
                    {
                        return this.filter(received);
                    }
                    catch (ObdException ex) 
                    {
                        this.logger.AddDebugMessage($"Query filter failed, error: {ex.Message}");
                    }
                }
            }

            throw new ObdException("Ran out of send attempts.", ObdExceptionReason.Timeout);
        }
    }
}
