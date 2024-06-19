using System;
using System.Collections.Generic;
using System.Text;

namespace PcmHacking
{
    public enum ObdExceptionReason
    {
        /// <summary>
        /// Unspecified error type - try to avoid using this.
        /// </summary>
        Error = 0,

        /// <summary>
        /// Response was shorter than expected.
        /// </summary>
        Truncated = 1,

        /// <summary>
        /// Response contained data that differs from what was expected.
        /// </summary>
        UnexpectedResponse = 2,

        /// <summary>
        /// No response was received before the timeout expired.
        /// </summary>
        Timeout = 3,

        /// <summary>
        /// The operation was cancelled by the user.
        /// </summary>
        Cancelled = 4,

        /// <summary>
        /// The request was refused.
        /// </summary>
        Refused = 5,
    }

    public class ObdException : Exception
    {
        public ObdExceptionReason Reason { get; private set; }

        public ObdException(string message, ObdExceptionReason reason, Exception innerException = null) : base(message, innerException)
        {
            this.Reason = reason;
        }
    }
}
