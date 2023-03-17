using System.Net;

namespace syslogToKusto
{
    internal class MessageForKusto
    {
        public string Payload { get; internal set; }
        public EndPoint RemoteEndPoint { get; internal set; }
        public string SyslogServerName { get; internal set; }
        public int ReceivedBytes { get; internal set; }
    }
}