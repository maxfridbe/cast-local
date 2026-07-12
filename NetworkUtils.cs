using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace CastBlueScreen
{
    public static class NetworkUtils
    {
        public static string GetLocalIpAddress(string targetIp)
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect(targetIp, 8009);
                    var endPoint = socket.LocalEndPoint as IPEndPoint;
                    if (endPoint != null)
                    {
                        return endPoint.Address.ToString();
                    }
                }
            }
            catch
            {
                // Catch any exception and fall back
            }

            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    var endPoint = socket.LocalEndPoint as IPEndPoint;
                    if (endPoint != null)
                    {
                        return endPoint.Address.ToString();
                    }
                }
            }
            catch
            {
                // Catch any exception and fall back to local address scan
            }

            var ip = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
            return ip?.ToString() ?? "127.0.0.1";
        }
    }
}
