using System;

namespace FtpServer.Exceptions
{
    public class FtpReplyException : Exception
    {
        public FtpReplyException(string reply) : base(reply) { }
    }
}
