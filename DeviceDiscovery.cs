using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace CastBlueScreen
{
    public static class DeviceDiscovery
    {
        public static async Task<bool> CheckPortAsync(string ip, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(ip, port);
                    var delayTask = Task.Delay(timeoutMs);
                    var completedTask = await Task.WhenAny(connectTask, delayTask);
                    if (completedTask == connectTask)
                    {
                        await connectTask; // propagates exceptions
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static async Task<string> GetEurekaDeviceNameAsync(string ip)
        {
            try
            {
                using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                {
                    var response = await httpClient.GetStringAsync($"http://{ip}:8008/setup/eureka_info");
                    using (var doc = JsonDocument.Parse(response))
                    {
                        if (doc.RootElement.TryGetProperty("name", out var nameProp))
                        {
                            return nameProp.GetString() ?? "";
                        }
                    }
                }
            }
            catch { }
            return "";
        }
    }
}
