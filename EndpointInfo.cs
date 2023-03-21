using System.Net;
using System.Net.Sockets;

namespace syslogToKusto
{
    public class EndpointInfo
    {
        public String Address { get; internal set; }
        public AddressFamily AddressFamily { get; internal set; }
        public int Port { get; internal set; }
    }
}