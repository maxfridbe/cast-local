using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using GoogleCast;
using GoogleCast.Channels;
using GoogleCast.Models.Media;
using System.Diagnostics;

namespace CastBlueScreen
{
    class Program
    {
        // Base64 for a 1x1 solid blue PNG image.
        private static readonly byte[] BluePngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
        );

        private static Stream? _pipedStream = null;
        private static byte[]? _preReadHeader = null;
        private static int _preReadLength = 0;
        private static CachedPipelineStream? _liveCacher = null;
        private static long? _contentSize = null;

        private static string? _sourceFilePath = null;
        private static long _sourceFileSize = 0;
        private static double _sourceDuration = 0;
        private static string? _tempFilePath = null;
        private static string? _hlsDir = null;
        private static Process? _transcodeProcess = null;
        private static double _currentSeekSeconds = 0;
        private static SemaphoreSlim _transcodeLock = new SemaphoreSlim(1, 1);
        private static bool _isLiveMode = false;

        static async Task Main(string[] args)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("========================================");
            Console.WriteLine("        Smart TV Cast Utility           ");
            Console.WriteLine("========================================");
            Console.ResetColor();

            string? targetIp = null;
            int? ccIndex = null;
            long? contentSize = null;
            bool isLiveFlag = false;

            // Parse arguments
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--cc" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int idx))
                    {
                        ccIndex = idx;
                    }
                    i++; // skip next arg
                }
                else if (args[i] == "--size" && i + 1 < args.Length)
                {
                    if (long.TryParse(args[i + 1], out long sz))
                    {
                        contentSize = sz;
                    }
                    i++;
                }
                else if (args[i] == "--live")
                {
                    isLiveFlag = true;
                }
                else if (IPAddress.TryParse(args[i], out _))
                {
                    targetIp = args[i];
                }
                else
                {
                    if (File.Exists(args[i]))
                    {
                        _sourceFilePath = Path.GetFullPath(args[i]);
                    }
                }
            }

            _contentSize = contentSize;
            _isLiveMode = isLiveFlag;

            byte[] imageBytes = BluePngBytes;
            bool isPiped = Console.IsInputRedirected || isLiveFlag;
            bool isVideo = false;

            if (_sourceFilePath != null)
            {
                isVideo = true;
                isPiped = false;
                _sourceFileSize = new FileInfo(_sourceFilePath).Length;
                _sourceDuration = await GetVideoDurationAsync(_sourceFilePath);
                Console.WriteLine($"[Info] Local file transcoding proxy mode initialized for: {_sourceFilePath}");
                Console.WriteLine($"[Info] File Size: {_sourceFileSize} bytes, Duration: {_sourceDuration:F2} seconds");

                if (!_isLiveMode)
                {
                    // HLS mode (default for local files): transcode to an HLS event playlist.
                    // The Chromecast default receiver plays HLS natively with a real, growing
                    // timeline, so remote seeking works without any interception hacks.
                    _hlsDir = Path.Combine(Path.GetTempPath(), "cast_hls_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_hlsDir);
                    Console.WriteLine($"[Info] Initializing background HLS transcoding to: {_hlsDir}");

                    var hlsStartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-i \"{_sourceFilePath}\" -map 0:v:0 -map 0:a:0 -sn -c:v copy -c:a aac -ac 2 " +
                                    $"-f hls -hls_time 4 -hls_playlist_type event -hls_flags independent_segments " +
                                    $"-hls_segment_filename \"{Path.Combine(_hlsDir, "seg%05d.ts")}\" -y \"{Path.Combine(_hlsDir, "index.m3u8")}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    _transcodeProcess = Process.Start(hlsStartInfo);

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[Info] Waiting for initial HLS segments before casting...");
                    Console.ResetColor();

                    string playlistPath = Path.Combine(_hlsDir, "index.m3u8");
                    while (true)
                    {
                        if (File.Exists(playlistPath))
                        {
                            int segmentCount = 0;
                            try
                            {
                                segmentCount = File.ReadAllLines(playlistPath).Count(l => l.StartsWith("#EXTINF"));
                            }
                            catch { }
                            Console.Write($"\r[Pre-buffer] {segmentCount} segment(s) ready...");
                            if (segmentCount >= 3) break;
                        }
                        else
                        {
                            Console.Write("\r[Pre-buffer] Waiting for transcode stream to start...");
                        }

                        if (_transcodeProcess != null && _transcodeProcess.HasExited)
                        {
                            break;
                        }
                        await Task.Delay(250);
                    }
                    Console.WriteLine("\n[Info] Initial HLS segments ready! Launching cast...");
                }
                else
                {
                    _tempFilePath = Path.Combine(Path.GetTempPath(), "cast_temp_" + Guid.NewGuid().ToString() + ".mp4");
                    Console.WriteLine($"[Info] Initializing background transcoding to: {_tempFilePath}");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-i \"{_sourceFilePath}\" -map 0:v:0 -map 0:a:0 -sn -c:v copy -c:a aac -ac 2 -movflags frag_keyframe+empty_moov -y \"{_tempFilePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    _transcodeProcess = Process.Start(startInfo);

                    // Pre-buffer 15MB to prevent TV startup timeouts
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[Info] Pre-buffering transcode cache to ensure smooth TV startup. Please wait...");
                    Console.ResetColor();

                    long targetPrebuffer = Math.Min(15000000L, _sourceFileSize);
                    while (true)
                    {
                        if (File.Exists(_tempFilePath))
                        {
                            long currentSize = new FileInfo(_tempFilePath).Length;
                            if (currentSize >= targetPrebuffer)
                            {
                                break;
                            }
                            double progressPercent = (double)currentSize / targetPrebuffer * 100.0;
                            Console.Write($"\r[Pre-buffer] Progress: {progressPercent:F1}% ({currentSize / 1024 / 1024}MB / {targetPrebuffer / 1024 / 1024}MB)...");
                        }
                        else
                        {
                            Console.Write("\r[Pre-buffer] Waiting for transcode stream to start...");
                        }

                        if (_transcodeProcess != null && _transcodeProcess.HasExited)
                        {
                            break;
                        }
                        await Task.Delay(250);
                    }
                    Console.WriteLine("\n[Info] Pre-buffering complete! Launching cast...");
                }

                // Auto-select first device if not specified
                if (targetIp == null && ccIndex == null)
                {
                    ccIndex = 1;
                    Console.WriteLine("[Info] Local file mode: Auto-selecting first discovered device (--cc 1).");
                }
            }
            else if (isPiped)
            {
                Console.WriteLine("[Info] Reading stream header from standard input pipeline...");
                _pipedStream = Console.OpenStandardInput();
                _preReadHeader = new byte[8];

                // Read up to 8 bytes to detect the MIME type
                while (_preReadLength < _preReadHeader.Length)
                {
                    int read = await _pipedStream.ReadAsync(_preReadHeader, _preReadLength, _preReadHeader.Length - _preReadLength);
                    if (read <= 0) break;
                    _preReadLength += read;
                }

                if (_preReadLength > 0)
                {
                    string mimeType = GetMimeType(_preReadHeader, _preReadLength);
                    if (mimeType.StartsWith("image/"))
                    {
                        isVideo = false;
                        // For images, we buffer the whole stream in memory
                        using (var ms = new MemoryStream())
                        {
                            await ms.WriteAsync(_preReadHeader, 0, _preReadLength);
                            await _pipedStream.CopyToAsync(ms);
                            imageBytes = ms.ToArray();
                        }
                        Console.WriteLine($"[Info] Read {imageBytes.Length} bytes of static image from pipeline.");
                    }
                    else
                    {
                        isVideo = true;
                        // For video, we don't buffer! We'll stream the pipe directly.
                        imageBytes = _preReadHeader; // Fallback reference so other code compiles
                        _liveCacher = new CachedPipelineStream(_pipedStream, _preReadHeader, _preReadLength);
                        Console.WriteLine($"[Info] Detected video stream ({mimeType}) in pipeline. Streaming progressively...");
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[Warning] Pipeline was empty. Falling back to default blue screen.");
                    Console.ResetColor();
                    isPiped = false;
                }

                // If running in pipeline and no target is specified, auto-default to first discovered device
                if (targetIp == null && ccIndex == null)
                {
                    ccIndex = 1;
                    Console.WriteLine("[Info] Pipeline mode: Auto-selecting first discovered device (--cc 1).");
                }
            }

            Console.WriteLine("[Info] Scanning network interfaces and probing Living Room TV (192.168.50.109)...");
            List<IReceiver> receivers = new List<IReceiver>();
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                 ni.SupportsMulticast &&
                                 ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .ToList();

                var locator = new DeviceLocator();
                
                // 1. Kick off parallel mDNS scans on each interface
                var scanTasks = interfaces.Select(async ni =>
                {
                    try
                    {
                        var found = await locator.FindReceiversAsync(ni);
                        return found ?? Enumerable.Empty<IReceiver>();
                    }
                    catch
                    {
                        return Enumerable.Empty<IReceiver>();
                    }
                }).ToList();

                // 2. In parallel, probe the target TV IP 192.168.50.109 directly
                var probeTask = Task.Run(async () =>
                {
                    string targetTvIp = "192.168.50.109";
                    bool isTvOpen = await CheckPortAsync(targetTvIp, 8009, 1500);
                    if (isTvOpen)
                    {
                        string name = await GetEurekaDeviceNameAsync(targetTvIp);
                        if (string.IsNullOrEmpty(name))
                        {
                            name = "Living Room TV";
                        }
                        IReceiver probedReceiver = new Receiver
                        {
                            Id = Guid.NewGuid().ToString(),
                            FriendlyName = name,
                            IPEndPoint = new IPEndPoint(IPAddress.Parse(targetTvIp), 8009)
                        };
                        return new List<IReceiver> { probedReceiver };
                    }
                    return new List<IReceiver>();
                });

                // Await all discovery methods
                await Task.WhenAll(scanTasks);
                var probedList = await probeTask;

                var allFound = new List<IReceiver>();
                foreach (var task in scanTasks)
                {
                    allFound.AddRange(task.Result);
                }
                allFound.AddRange(probedList);

                // De-duplicate discovered devices by IP address
                receivers = allFound
                    .Where(r => r != null && r.IPEndPoint != null)
                    .GroupBy(r => r.IPEndPoint!.Address.ToString())
                    .Select(g => g.First())
                    .ToList();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[Warning] Discovery scan encountered errors: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine($"[Info] Discovery complete. Found {receivers.Count} device(s).");

            if (ccIndex.HasValue)
            {
                if (receivers.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[Error] --cc option specified, but no devices were discovered.");
                    Console.ResetColor();
                    return;
                }

                if (ccIndex.Value < 1 || ccIndex.Value > receivers.Count)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Error] --cc {ccIndex.Value} is out of range. Please choose an index between 1 and {receivers.Count}.");
                    Console.ResetColor();
                    return;
                }

                var selectedReceiver = receivers[ccIndex.Value - 1];
                targetIp = selectedReceiver.IPEndPoint?.Address.ToString();
                Console.WriteLine($"[Info] Selected device --cc {ccIndex.Value}: {selectedReceiver.FriendlyName} ({targetIp})");
            }
            else if (targetIp == null)
            {
                if (receivers.Count > 0)
                {
                    Console.WriteLine("\nDiscovered devices:");
                    for (int i = 0; i < receivers.Count; i++)
                    {
                        Console.WriteLine($"  [{i + 1}] {receivers[i].FriendlyName} ({receivers[i].IPEndPoint})");
                    }
                    Console.WriteLine($"  [{receivers.Count + 1}] Enter IP address manually");

                    Console.Write($"\nSelect a device (1-{receivers.Count + 1}): ");
                    string? choiceInput = Console.ReadLine();
                    if (int.TryParse(choiceInput, out int choice) && choice >= 1 && choice <= receivers.Count)
                    {
                        var endPoint = receivers[choice - 1].IPEndPoint;
                        if (endPoint != null)
                        {
                            targetIp = endPoint.Address.ToString();
                        }
                    }
                }

                if (targetIp == null)
                {
                    Console.Write("\nPlease enter the IP address of your TV manually (e.g., 192.168.1.50): ");
                    string? ipInput = Console.ReadLine();
                    while (string.IsNullOrWhiteSpace(ipInput) || !IPAddress.TryParse(ipInput.Trim(), out _))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write("Invalid IP address. Please enter a valid IPv4 address: ");
                        Console.ResetColor();
                        ipInput = Console.ReadLine();
                    }
                    targetIp = ipInput.Trim();
                }
            }

            Console.WriteLine($"\n[Info] Target TV IP: {targetIp}");

            if (targetIp == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Error] No target IP address specified.");
                Console.ResetColor();
                return;
            }

            // Find local active IP to host our web server
            string localIp = GetLocalIpAddress(targetIp);
            Console.WriteLine($"[Info] Local IP Address: {localIp}");

            // Start simple HTTP Server to host the blue image
            HttpListener? listener = null;
            int port = 0;
            try
            {
                (listener, port) = StartHttpServer(localIp);
                Console.WriteLine($"[Info] Web server started at http://{localIp}:{port}/media.png");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] Failed to start local web server: {ex.Message}");
                Console.ResetColor();
                return;
            }



            // Run the HTTP server request processing in the background
            _ = Task.Run(() => RunHttpServerAsync(listener, imageBytes));

            // Connect to Chromecast and Cast the Image
            Console.WriteLine("[Info] Connecting to the TV...");
            var sender = new Sender();
            try
            {
                var receiver = new Receiver
                {
                    Id = Guid.NewGuid().ToString(),
                    FriendlyName = "Selected Connection",
                    IPEndPoint = new IPEndPoint(IPAddress.Parse(targetIp), 8009)
                };

                await sender.ConnectAsync(receiver);
                Console.WriteLine("[Info] Launching default media receiver on the TV... ");
                
                var mediaChannel = sender.GetChannel<IMediaChannel>();
                await sender.LaunchAsync(mediaChannel);

                string mimeType = _hlsDir != null ? "application/x-mpegURL" : (_sourceFilePath != null ? "video/mp4" : (isPiped ? GetMimeType(imageBytes, _preReadLength) : GetMimeType(imageBytes)));

                string extension = mimeType switch
                {
                    "application/x-mpegURL" => "m3u8",
                    "video/mp4" => "mp4",
                    "video/webm" => "webm",
                    "image/jpeg" => "jpg",
                    "image/gif" => "gif",
                    _ => "png"
                };

                string imageUri = $"http://{localIp}:{port}/media.{extension}?t={DateTime.UtcNow.Ticks}";
                Console.WriteLine($"[Info] Casting media URL: {imageUri} (MIME: {mimeType})");

                StreamType streamType = StreamType.None;
                if (isVideo)
                {
                    streamType = (_sourceFilePath != null && !_isLiveMode) ? StreamType.Buffered : StreamType.Live;
                }

                MediaStatus? mediaStatus = null;
                try
                {
                    mediaStatus = await mediaChannel.LoadAsync(new MediaInformation
                    {
                        ContentId = imageUri,
                        ContentType = mimeType,
                        StreamType = streamType
                    });
                }
                catch (TimeoutException)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[Warning] Cast load request timed out, but TV is still loading the stream. Continuing...");
                    Console.ResetColor();
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n========================================================");
                Console.WriteLine(" [Success] Cast request sent successfully!");
                if (_sourceFilePath != null)
                {
                    Console.WriteLine($" The TV should now play local video: {Path.GetFileName(_sourceFilePath)}");
                }
                else if (isPiped)
                {
                    Console.WriteLine(isVideo ? " The TV should now play your piped video." : " The TV should now display your piped image.");
                }
                else
                {
                    Console.WriteLine(" The TV should now display a blue screen.");
                }
                Console.WriteLine("========================================================");
                Console.ResetColor();

                var tcs = new TaskCompletionSource<bool>();

                // Poll for playback and connection status in the background
                _ = Task.Run(async () =>
                {
                    double lastCurrentTime = 0;
                    DateTime lastCheckTime = DateTime.UtcNow;
                    string lastPlayerState = "IDLE";
                    bool isFirstStatus = true;

                    while (!tcs.Task.IsCompleted)
                    {
                        await Task.Delay(1000);
                        try
                        {
                            var status = await mediaChannel.GetStatusAsync();
                            if (status != null)
                            {
                                if (isVideo && status.PlayerState == "IDLE" && (status.IdleReason == "FINISHED" || status.IdleReason == "ERROR"))
                                {
                                    Console.WriteLine($"\n[Info] Video playback finished ({status.IdleReason}). Auto-exiting...");
                                    tcs.TrySetResult(true);
                                }

                                DateTime now = DateTime.UtcNow;
                                // HLS mode: the receiver seeks natively within the playlist; no interception needed.
                                if (_hlsDir == null && status.PlayerState == "PLAYING" && !isFirstStatus && lastPlayerState == "PLAYING")
                                {
                                    double timeDelta = (now - lastCheckTime).TotalSeconds;
                                    double expectedTime = lastCurrentTime + timeDelta;
                                    double actualTime = status.CurrentTime;

                                    if (Math.Abs(actualTime - expectedTime) > 5.0)
                                    {
                                        Console.ForegroundColor = ConsoleColor.Yellow;
                                        Console.WriteLine($"\n[Info] Seek detected on TV: jump from {lastCurrentTime:F1}s to {actualTime:F1}s.");
                                        Console.ResetColor();

                                        string newUrl = $"http://{localIp}:{port}/media.mp4?seek={actualTime:F2}&t={now.Ticks}";
                                        Console.WriteLine($"[Info] Reloading TV media with seeked URL: {newUrl}");

                                        _ = Task.Run(async () =>
                                        {
                                            try
                                            {
                                                await mediaChannel.LoadAsync(new MediaInformation
                                                {
                                                    ContentId = newUrl,
                                                    ContentType = mimeType,
                                                    StreamType = streamType
                                                });
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine($"[Warning] Failed to reload media for seek: {ex.Message}");
                                            }
                                        });
                                    }
                                }

                                lastCurrentTime = status.CurrentTime;
                                lastCheckTime = now;
                                lastPlayerState = status.PlayerState;
                                isFirstStatus = false;
                            }
                        }
                        catch (Exception)
                        {
                            // Cast control channel disconnected or went idle. Keep HTTP server running.
                        }
                    }
                });

                // Set up Ctrl+C listener
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    tcs.TrySetResult(true);
                };

                if (isPiped)
                {
                    Console.WriteLine(isVideo 
                        ? "Casting piped video. Keep this process running to stream. Press [Ctrl+C] to stop and exit..." 
                        : "Casting piped image. Press [Ctrl+C] to stop casting and exit...");
                    await tcs.Task;
                }
                else
                {
                    Console.WriteLine("Casting. Press [Enter] or [Ctrl+C] to stop casting and exit...");
                    var readLineTask = Task.Run(() => Console.ReadLine());
                    var completedTask = await Task.WhenAny(tcs.Task, readLineTask);
                    if (completedTask == readLineTask && readLineTask.Result == null)
                    {
                        // stdin is closed (e.g. running detached/background): don't exit on EOF,
                        // keep serving until playback finishes or the process is signalled.
                        Console.WriteLine("[Info] No interactive console detected. Running until playback finishes (Ctrl+C to stop).");
                        await tcs.Task;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[Error] Casting failed!");
                Console.WriteLine($"Details: {ex}");
                Console.WriteLine("\nTroubleshooting tips:");
                Console.WriteLine("1. Ensure your TV is powered on and connected to the same wireless network.");
                Console.WriteLine("2. Check that the IP address you entered is correct.");
                Console.WriteLine("3. Ensure that firewall settings on this machine are not blocking incoming requests or outgoing Chromecast connections (port 8009).");
                Console.ResetColor();
            }
            finally
            {
                if (_transcodeProcess != null)
                {
                    try { _transcodeProcess.Kill(); } catch { }
                }
                if (_tempFilePath != null && File.Exists(_tempFilePath))
                {
                    try { File.Delete(_tempFilePath); } catch { }
                }
                if (_hlsDir != null && Directory.Exists(_hlsDir))
                {
                    try { Directory.Delete(_hlsDir, true); } catch { }
                }

                Console.WriteLine("[Info] Stopping web server and disconnecting...");
                try
                {
                    if (listener != null)
                    {
                        listener.Stop();
                        listener.Close();
                    }
                }
                catch { }

                try
                {
                    sender.Disconnect();
                }
                catch { }
                
                Console.WriteLine("[Info] Exited.");
            }
        }

        static async Task<bool> CheckPortAsync(string ip, int port, int timeoutMs)
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

        static async Task<string> GetEurekaDeviceNameAsync(string ip)
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

        static string GetLocalIpAddress(string targetIp)
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

        static (HttpListener listener, int port) StartHttpServer(string localIp)
        {
            int port = 8080;
            while (port < 9000)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://*:{port}/");
                    listener.Start();
                    return (listener, port);
                }
                catch
                {
                    try
                    {
                        var listener = new HttpListener();
                        listener.Prefixes.Add($"http://{localIp}:{port}/");
                        listener.Start();
                        return (listener, port);
                    }
                    catch
                    {
                        port++;
                    }
                }
            }
            throw new Exception("Could not find an available port to start the HTTP server.");
        }

        static async Task RunHttpServerAsync(HttpListener listener, byte[] imageBytes)
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    if (context == null) continue;
                    var request = context.Request;
                    var response = context.Response;

                    if (request != null && response != null)
                    {
                        Console.WriteLine($"[HTTP Server] Request received for: {request.RawUrl}");

                        string? url = request.RawUrl;
                        if (url != null)
                        {
                            string mimeType = _sourceFilePath != null ? "video/mp4" : (_pipedStream != null ? GetMimeType(imageBytes, _preReadLength) : GetMimeType(imageBytes));

                            if (_hlsDir != null)
                            {
                                await ServeHlsAsync(request, response);
                            }
                            else if (_sourceFilePath != null)
                            {
                                long totalSize = _sourceFileSize;
                                response.ContentType = mimeType;
                                response.AddHeader("Access-Control-Allow-Origin", "*");
                                if (!_isLiveMode)
                                {
                                    response.AddHeader("Accept-Ranges", "bytes");
                                }

                                long start = 0;
                                long end = totalSize - 1;
                                bool isPartial = false;

                                if (!_isLiveMode)
                                {
                                    string? rangeHeader = request.Headers["Range"];
                                    if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                                    {
                                        var parts = rangeHeader.Substring(6).Split('-');
                                        if (parts.Length > 0 && long.TryParse(parts[0], out long parsedStart))
                                        {
                                            start = parsedStart;
                                            isPartial = true;
                                        }
                                        if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) && long.TryParse(parts[1], out long parsedEnd))
                                        {
                                            end = parsedEnd;
                                        }
                                    }
                                }

                                // Safety bounds checks
                                if (start < 0) start = 0;
                                if (end >= totalSize) end = totalSize - 1;
                                if (start > end) start = end;

                                if (isPartial && !_isLiveMode)
                                {
                                    response.StatusCode = (int)HttpStatusCode.PartialContent;
                                    response.ContentLength64 = end - start + 1;
                                    response.AddHeader("Content-Range", $"bytes {start}-{end}/{totalSize}");
                                    Console.WriteLine($"[HTTP Server] Serving range: bytes {start}-{end}/{totalSize} (dynamic transcode)");
                                }
                                else
                                {
                                    response.StatusCode = (int)HttpStatusCode.OK;
                                    if (!_isLiveMode)
                                    {
                                        response.ContentLength64 = totalSize;
                                    }
                                    Console.WriteLine(_isLiveMode ? "[HTTP Server] Serving live stream (progressive transcode)" : $"[HTTP Server] Serving full file from beginning: {totalSize} bytes (dynamic transcode)");
                                }

                                 // Seek parameter checking and on-demand transcode restarting
                                 string? seekParam = request.QueryString["seek"];
                                 if (!string.IsNullOrEmpty(seekParam) && double.TryParse(seekParam, out double querySeek))
                                 {
                                     await _transcodeLock.WaitAsync();
                                     try
                                     {
                                         if (Math.Abs(querySeek - _currentSeekSeconds) > 2.0)
                                         {
                                             Console.WriteLine($"[HTTP Server] Restarting background transcoding from seek point: {querySeek:F2} seconds...");

                                             if (_transcodeProcess != null)
                                             {
                                                 try { _transcodeProcess.Kill(); } catch { }
                                             }

                                             await Task.Delay(200);
                                             if (_tempFilePath != null && File.Exists(_tempFilePath))
                                             {
                                                 try { File.Delete(_tempFilePath); } catch { }
                                             }

                                             _currentSeekSeconds = querySeek;

                                             var startInfo = new ProcessStartInfo
                                             {
                                                 FileName = "ffmpeg",
                                                 Arguments = $"-ss {querySeek:F2} -i \"{_sourceFilePath}\" -map 0:v:0 -map 0:a:0 -sn -c:v copy -c:a aac -ac 2 -movflags frag_keyframe+empty_moov -y \"{_tempFilePath}\"",
                                                 UseShellExecute = false,
                                                 CreateNoWindow = true
                                             };

                                             _transcodeProcess = Process.Start(startInfo);

                                             // Wait up to 5 seconds for at least 5MB of new stream data to buffer
                                             long targetPrebuffer = 5000000;
                                             var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                                             while (stopwatch.ElapsedMilliseconds < 5000)
                                             {
                                                 if (File.Exists(_tempFilePath))
                                                 {
                                                     long currentSize = new FileInfo(_tempFilePath).Length;
                                                     if (currentSize >= targetPrebuffer)
                                                     {
                                                         break;
                                                     }
                                                 }
                                                 if (_transcodeProcess != null && _transcodeProcess.HasExited)
                                                 {
                                                     break;
                                                 }
                                                 await Task.Delay(100);
                                             }
                                             Console.WriteLine($"[HTTP Server] Pre-buffering for seek complete.");
                                         }
                                     }
                                     finally
                                     {
                                         _transcodeLock.Release();
                                     }
                                 }

                                 // Read and serve from the local growing transcode file
                                 if (_tempFilePath != null && File.Exists(_tempFilePath))
                                 {
                                     try
                                     {
                                         using (var output = response.OutputStream)
                                         {
                                             if (output != null)
                                             {
                                                 using (var fs = new FileStream(_tempFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                                 {
                                                     fs.Seek(start, SeekOrigin.Begin);
                                                     byte[] buffer = new byte[65536];
                                                     long bytesRemaining = (end - start + 1);

                                                     var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                                                     long totalBytesSent = 0;
                                                     // Throttle at 3x the source's average byte rate (floor 700 KB/s) so playback never starves
                                                     double sourceByteRate = _sourceDuration > 0 ? _sourceFileSize / _sourceDuration : 700 * 1024;
                                                     double maxBytesPerSecond = Math.Max(700 * 1024, sourceByteRate * 3);

                                                     while (bytesRemaining > 0)
                                                     {
                                                         int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                                                         int read = fs.Read(buffer, 0, toRead);

                                                         if (read > 0)
                                                         {
                                                             await output.WriteAsync(buffer, 0, read);
                                                             bytesRemaining -= read;
                                                             totalBytesSent += read;

                                                             // Only throttle after the first 5MB of this request
                                                             if (totalBytesSent > 5000000)
                                                             {
                                                                 double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                                                                 if (elapsedSeconds > 0)
                                                                 {
                                                                     double currentRate = totalBytesSent / elapsedSeconds;
                                                                     if (currentRate > maxBytesPerSecond)
                                                                     {
                                                                         double targetTime = totalBytesSent / maxBytesPerSecond;
                                                                         double sleepTimeMs = (targetTime - elapsedSeconds) * 1000.0;
                                                                         if (sleepTimeMs > 10)
                                                                         {
                                                                             await Task.Delay((int)sleepTimeMs);
                                                                         }
                                                                     }
                                                                 }
                                                             }
                                                         }
                                                         else
                                                         {
                                                             // Reached current EOF, wait for more data from background transcoding
                                                             if (_transcodeProcess != null && !_transcodeProcess.HasExited)
                                                              {
                                                                 await Task.Delay(100);
                                                              }
                                                             else
                                                             {
                                                                 break;
                                                             }
                                                         }
                                                     }
                                                 }
                                             }
                                         }
                                     }
                                     catch (Exception ex)
                                     {
                                         Console.WriteLine($"[HTTP Server] Local transcode stream serving interrupted: {ex.Message}");
                                     }
                                 }
                            }
                            else if (_pipedStream != null && mimeType.StartsWith("video/") && _liveCacher != null)
                            {
                                long totalSize = _contentSize ?? 1000000000L; // Default to 1 GB if not specified

                                response.ContentType = mimeType;
                                response.AddHeader("Access-Control-Allow-Origin", "*");
                                response.AddHeader("Accept-Ranges", "bytes");

                                long start = 0;
                                long end = totalSize - 1;
                                bool isPartial = false;

                                string? rangeHeader = request.Headers["Range"];
                                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                                {
                                    var parts = rangeHeader.Substring(6).Split('-');
                                    if (parts.Length > 0 && long.TryParse(parts[0], out long parsedStart))
                                    {
                                        start = parsedStart;
                                        isPartial = true;
                                    }
                                    if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) && long.TryParse(parts[1], out long parsedEnd))
                                    {
                                        end = parsedEnd;
                                    }
                                }

                                // Safety bounds checks
                                if (start < 0) start = 0;
                                if (end >= totalSize) end = totalSize - 1;
                                if (start > end) start = end;

                                if (isPartial)
                                {
                                    response.StatusCode = (int)HttpStatusCode.PartialContent;
                                    response.ContentLength64 = end - start + 1;
                                    response.AddHeader("Content-Range", $"bytes {start}-{end}/{totalSize}");
                                    Console.WriteLine($"[HTTP Server] Streaming video starting from range offset: {start}-{end}/{totalSize} (live mode)...");
                                }
                                else
                                {
                                    response.StatusCode = (int)HttpStatusCode.OK;
                                    response.ContentLength64 = totalSize;
                                    Console.WriteLine($"[HTTP Server] Streaming video from beginning: {totalSize} bytes (live mode)...");
                                }

                                try
                                {
                                    using (var output = response.OutputStream)
                                    {
                                        if (output != null)
                                        {
                                            await _liveCacher.CopyToAsync(output, start);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[HTTP Server] Live stream connection interrupted: {ex.Message}");
                                }
                            }
                            else
                            {
                                // Static content serving (images or buffered content)
                                long start = 0;
                                long end = imageBytes.Length - 1;
                                bool isPartial = false;

                                string? rangeHeader = request.Headers["Range"];
                                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                                {
                                    var parts = rangeHeader.Substring(6).Split('-');
                                    if (parts.Length > 0 && long.TryParse(parts[0], out long parsedStart))
                                    {
                                        start = parsedStart;
                                        isPartial = true;
                                    }
                                    if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) && long.TryParse(parts[1], out long parsedEnd))
                                    {
                                        end = parsedEnd;
                                    }
                                }

                                // Safety checks for range bounds
                                if (start < 0) start = 0;
                                if (end >= imageBytes.Length) end = imageBytes.Length - 1;
                                if (start > end) start = end;

                                response.ContentType = mimeType;
                                response.AddHeader("Access-Control-Allow-Origin", "*");
                                response.AddHeader("Accept-Ranges", "bytes");

                                if (isPartial)
                                {
                                    response.StatusCode = (int)HttpStatusCode.PartialContent;
                                    response.ContentLength64 = end - start + 1;
                                    response.AddHeader("Content-Range", $"bytes {start}-{end}/{imageBytes.Length}");
                                    Console.WriteLine($"[HTTP Server] Serving range: bytes {start}-{end}/{imageBytes.Length} (Partial Content)");
                                }
                                else
                                {
                                    response.StatusCode = (int)HttpStatusCode.OK;
                                    response.ContentLength64 = imageBytes.Length;
                                    Console.WriteLine($"[HTTP Server] Serving full file: {imageBytes.Length} bytes (OK)");
                                }

                                using (var output = response.OutputStream)
                                {
                                    if (output != null)
                                    {
                                        await output.WriteAsync(imageBytes, (int)start, (int)(end - start + 1));
                                    }
                                }
                            }
                        }
                        else
                        {
                            response.StatusCode = (int)HttpStatusCode.NotFound;
                            response.Close();
                        }
                    }
                }
                catch (Exception)
                {
                    if (!listener.IsListening) break;
                }
            }
        }

        static async Task ServeHlsAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            response.AddHeader("Access-Control-Allow-Origin", "*");
            response.AddHeader("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
            response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Range");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }

            string requestedFile = Path.GetFileName(request.Url?.AbsolutePath ?? "/") ?? "";
            // Any *.m3u8 request (e.g. our cache-busted /media.m3u8) maps to the ffmpeg playlist.
            if (requestedFile.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || requestedFile.Length == 0)
            {
                requestedFile = "index.m3u8";
            }

            string fullPath = Path.Combine(_hlsDir!, requestedFile);
            if (!File.Exists(fullPath))
            {
                Console.WriteLine($"[HTTP Server] HLS file not found: {requestedFile}");
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            bool isPlaylist = requestedFile.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            response.ContentType = isPlaylist ? "application/vnd.apple.mpegurl" : "video/mp2t";
            if (isPlaylist)
            {
                // The event playlist grows while transcoding; the receiver must always re-fetch it.
                response.AddHeader("Cache-Control", "no-cache, no-store");
            }

            try
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(fullPath);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentLength64 = fileBytes.Length;
                Console.WriteLine($"[HTTP Server] Serving HLS {(isPlaylist ? "playlist" : "segment")}: {requestedFile} ({fileBytes.Length} bytes)");
                using (var output = response.OutputStream)
                {
                    await output.WriteAsync(fileBytes, 0, fileBytes.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTP Server] HLS serving interrupted for {requestedFile}: {ex.Message}");
                try { response.Abort(); } catch { }
            }
        }

        static string GetMimeType(byte[] bytes)
        {
            return GetMimeType(bytes, bytes.Length);
        }

        static string GetMimeType(byte[] bytes, int length)
        {
            if (length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }
            if (length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }
            if (length >= 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            {
                return "image/gif";
            }
            // MP4 check: "ftyp" at bytes [4..7]
            if (length >= 8 && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
            {
                return "video/mp4";
            }
            // WebM check: starts with 1A 45 DF A3 (EBML header)
            if (length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
            {
                return "video/webm";
            }
            return "video/mp4"; // default fallback for piped video streams
        }

        static async Task<double> GetVideoDurationAsync(string filePath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                      {
                        string output = await process.StandardOutput.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        if (double.TryParse(output.Trim(), out double duration))
                        {
                            return duration;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }
    }

    class CachedPipelineStream
    {
        private readonly Stream _underlying;
        private readonly MemoryStream _cache = new MemoryStream();
        private readonly object _lock = new object();
        private bool _isUnderlyingExhausted = false;

        public CachedPipelineStream(Stream underlying, byte[] initialHeader, int initialLength)
        {
            _underlying = underlying;
            if (initialLength > 0)
            {
                _cache.Write(initialHeader, 0, initialLength);
            }
        }

        public async Task CopyToAsync(Stream destination, long startOffset)
        {
            long currentOffset = startOffset;
            byte[] buffer = new byte[81920]; // 80KB buffer

            while (true)
            {
                byte[]? chunkToCopy = null;
                int chunkLen = 0;

                lock (_lock)
                {
                    if (currentOffset < _cache.Length)
                    {
                        // Serve from cache
                        int available = (int)(_cache.Length - currentOffset);
                        chunkLen = Math.Min(buffer.Length, available);
                        _cache.Position = currentOffset;
                        _cache.Read(buffer, 0, chunkLen);

                        chunkToCopy = buffer;
                    }
                }

                if (chunkToCopy != null)
                {
                    await destination.WriteAsync(chunkToCopy, 0, chunkLen);
                    currentOffset += chunkLen;
                    continue;
                }

                // If cache is exhausted but underlying stream is also exhausted, we are done!
                lock (_lock)
                {
                    if (_isUnderlyingExhausted) break;
                }

                // Otherwise, read new bytes from the underlying stream
                int read = await _underlying.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    lock (_lock)
                    {
                        _isUnderlyingExhausted = true;
                    }
                    break;
                }

                lock (_lock)
                {
                    _cache.Position = _cache.Length;
                    _cache.Write(buffer, 0, read);
                }
            }
        }
    }
}
