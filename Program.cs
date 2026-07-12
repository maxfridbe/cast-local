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
        static async Task Main(string[] args)
        {
            if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
            {
                ShowHelp();
                return;
            }

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
            bool scanMode = false;
            bool previewMode = false;
            bool isWebUrl = false;
            string resolutionStr = "1920x1080";
            string durationStr = "30s";
            string delayStr = "0s";

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
                else if (args[i] == "--scan")
                {
                    scanMode = true;
                }
                else if (args[i] == "--preview" || args[i] == "-p")
                {
                    previewMode = true;
                }
                else if ((args[i] == "--resolution" || args[i] == "-r") && i + 1 < args.Length)
                {
                    resolutionStr = args[i + 1];
                    i++;
                }
                else if ((args[i] == "--duration" || args[i] == "-d") && i + 1 < args.Length)
                {
                    durationStr = args[i + 1];
                    i++;
                }
                else if ((args[i] == "--delay" || args[i] == "-y") && i + 1 < args.Length)
                {
                    delayStr = args[i + 1];
                    i++;
                }
                else if (args[i].StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                         args[i].StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    MediaServer._sourceFilePath = args[i];
                    isWebUrl = true;
                }
                else if (IPAddress.TryParse(args[i], out _))
                {
                    targetIp = args[i];
                }
                else
                {
                    MediaServer._sourceFilePath = Path.GetFullPath(args[i]);
                    isWebUrl = false;
                }
            }

            MediaServer._contentSize = contentSize;
            MediaServer._isLiveMode = isLiveFlag;

            double delaySeconds = 0;
            if (delayStr.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                delayStr = delayStr.Substring(0, delayStr.Length - 1);
            }
            if (double.TryParse(delayStr, out double del))
            {
                delaySeconds = del;
            }

            byte[] imageBytes = MediaServer.BluePngBytes;
            bool isVideo = false;
            bool isAudio = false;

            if (MediaServer._sourceFilePath != null)
            {
                if (!isWebUrl && !File.Exists(MediaServer._sourceFilePath))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Error] Source file not found: {MediaServer._sourceFilePath}");
                    Console.ResetColor();
                    return;
                }

                string inputExt = isWebUrl ? "" : Path.GetExtension(MediaServer._sourceFilePath).ToLowerInvariant();
                if (isWebUrl || inputExt == ".svg" || inputExt == ".html" || inputExt == ".htm")
                {
                    int width = 1920;
                    int height = 1080;
                    var parts = resolutionStr.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                    {
                        width = w;
                        height = h;
                    }

                    double durationSeconds = 30;
                    if (durationStr.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                    {
                        durationStr = durationStr.Substring(0, durationStr.Length - 1);
                    }
                    if (double.TryParse(durationStr, out double dur))
                    {
                        durationSeconds = dur;
                    }

                    if (MediaServer._isLiveMode)
                    {
                        MediaServer._isTranscoding = true;
                        MediaServer._hlsDir = Path.Combine(Path.GetTempPath(), "cast_live_hls_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(MediaServer._hlsDir);
                        MediaServer._tempFilePath = Path.Combine(MediaServer._hlsDir, "index.m3u8");
                        Console.WriteLine($"[Info] {(isWebUrl ? "Web URL" : "HTML/SVG")} live source detected. Initializing background HLS render to: {MediaServer._hlsDir}");

                        string targetRenderPath = MediaServer._tempFilePath;
                        string sourceInput = MediaServer._sourceFilePath;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await FfmpegUtils.RenderWebPageToMp4Async(sourceInput, targetRenderPath, width, height, double.PositiveInfinity, delaySeconds);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"\n[Error] Live web render crashed: {ex.Message}");
                            }
                        });

                        MediaServer._renderedHtmlPath = MediaServer._tempFilePath;
                        MediaServer._sourceFilePath = MediaServer._tempFilePath;

                        // Wait for initial HLS segments
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("[Info] Waiting for initial HLS segments before casting...");
                        Console.ResetColor();

                        string playlistPath = MediaServer._tempFilePath;
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < 15000)
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
                                if (segmentCount >= 2) break;
                            }
                            await Task.Delay(250);
                        }
                        Console.WriteLine();
                    }
                    else
                    {
                        string compiledMp4 = Path.Combine(Path.GetTempPath(), "rendered_web_" + Guid.NewGuid().ToString("N") + ".mp4");
                        try
                        {
                            Console.WriteLine($"[Info] {(isWebUrl ? "Web URL" : "HTML/SVG")} source detected. Rendering page to MP4 at {width}x{height} for {durationSeconds} seconds...");
                            await FfmpegUtils.RenderWebPageToMp4Async(MediaServer._sourceFilePath, compiledMp4, width, height, durationSeconds, delaySeconds);
                            
                            MediaServer._renderedHtmlPath = compiledMp4;
                            MediaServer._sourceFilePath = compiledMp4;
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"[Error] Failed to render HTML/SVG/URL source: {ex.Message}");
                            Console.ResetColor();
                            if (File.Exists(compiledMp4)) File.Delete(compiledMp4);
                            return;
                        }
                    }
                }

                bool hasVideo = true;
                if (MediaServer._isLiveMode && MediaServer._renderedHtmlPath != null)
                {
                    hasVideo = true;
                }
                else
                {
                    hasVideo = await FfmpegUtils.HasVideoAsync(MediaServer._sourceFilePath);
                }

                if (hasVideo)
                {
                    isVideo = true;
                    MediaServer._isAudioOnly = false;
                }
                else
                {
                    isAudio = true;
                    MediaServer._isAudioOnly = true;
                }

                if (MediaServer._isLiveMode && MediaServer._renderedHtmlPath != null)
                {
                    MediaServer._sourceFileSize = 0;
                    MediaServer._sourceDuration = double.PositiveInfinity;
                    Console.WriteLine($"[Info] Local live web render mode initialized for: {MediaServer._sourceFilePath}");
                }
                else
                {
                    MediaServer._sourceFileSize = new FileInfo(MediaServer._sourceFilePath).Length;
                    MediaServer._sourceDuration = await FfmpegUtils.GetVideoDurationAsync(MediaServer._sourceFilePath);
                    Console.WriteLine($"[Info] Local {(isVideo ? "video" : "audio")} transcoding proxy mode initialized for: {MediaServer._sourceFilePath}");
                    Console.WriteLine($"[Info] File Size: {MediaServer._sourceFileSize} bytes, Duration: {MediaServer._sourceDuration:F2} seconds");
                }

                bool isNativeAudio = false;
                if (isAudio)
                {
                    string ext = Path.GetExtension(MediaServer._sourceFilePath).ToLowerInvariant();
                    isNativeAudio = ext == ".mp3" || ext == ".aac" || ext == ".wav" || ext == ".flac" || ext == ".ogg" || ext == ".m4a";
                }

                if (isNativeAudio)
                {
                    MediaServer._tempFilePath = MediaServer._sourceFilePath;
                    MediaServer._isTranscoding = false;
                    Console.WriteLine($"[Info] Direct streaming mode initialized for native audio: {MediaServer._sourceFilePath}");
                }
                else if (!MediaServer._isLiveMode && !MediaServer._isAudioOnly)
                {
                    MediaServer._isTranscoding = true;
                    // HLS mode (default for local files): transcode to an HLS event playlist.
                    // The Chromecast default receiver plays HLS natively with a real, growing
                    // timeline, so remote seeking works without any interception hacks.
                    MediaServer._hlsDir = Path.Combine(Path.GetTempPath(), "cast_hls_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(MediaServer._hlsDir);
                    Console.WriteLine($"[Info] Initializing background HLS transcoding to: {MediaServer._hlsDir}");

                    string ffmpegMapArgs = MediaServer._isAudioOnly 
                        ? "-map 0:a:0 -vn -c:a aac -ac 2" 
                        : "-map 0:v:0 -map 0:a:0? -sn -c:v copy -c:a aac -ac 2";

                    var hlsStartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-loglevel quiet -nostats -i \"{MediaServer._sourceFilePath}\" {ffmpegMapArgs} " +
                                    $"-f hls -hls_time 4 -hls_playlist_type event -hls_flags independent_segments " +
                                    $"-hls_segment_filename \"{Path.Combine(MediaServer._hlsDir, "seg%05d.ts")}\" -y \"{Path.Combine(MediaServer._hlsDir, "index.m3u8")}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    MediaServer._transcodeProcess = Process.Start(hlsStartInfo);

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[Info] Waiting for initial HLS segments before casting...");
                    Console.ResetColor();

                    string playlistPath = Path.Combine(MediaServer._hlsDir, "index.m3u8");
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

                        if (MediaServer._transcodeProcess != null && MediaServer._transcodeProcess.HasExited)
                        {
                            break;
                        }
                        await Task.Delay(250);
                    }
                    Console.WriteLine("\n[Info] Initial HLS segments ready! Launching cast...");
                }
                else if (MediaServer._renderedHtmlPath == null)
                {
                    MediaServer._isTranscoding = true;
                    if (MediaServer._isAudioOnly)
                    {
                        // Transcode to MP3 temp file
                        MediaServer._tempFilePath = Path.Combine(Path.GetTempPath(), "cast_temp_" + Guid.NewGuid().ToString("N") + ".mp3");
                        Console.WriteLine($"[Info] Initializing background audio transcoding to: {MediaServer._tempFilePath}");

                        var startInfo = new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = $"-loglevel quiet -nostats -i \"{MediaServer._sourceFilePath}\" -map 0:a:0 -vn -c:a libmp3lame -q:a 2 -y \"{MediaServer._tempFilePath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        MediaServer._transcodeProcess = Process.Start(startInfo);

                        // Audio pre-buffering is extremely fast, wait up to 1 second or until finished
                        var stopwatch = Stopwatch.StartNew();
                        while (stopwatch.ElapsedMilliseconds < 1000)
                        {
                            if (File.Exists(MediaServer._tempFilePath) && new FileInfo(MediaServer._tempFilePath).Length > 10000)
                            {
                                break;
                            }
                            if (MediaServer._transcodeProcess != null && MediaServer._transcodeProcess.HasExited)
                            {
                                break;
                            }
                            await Task.Delay(100);
                        }
                    }
                    else
                    {
                        // Video progressive transcoding
                        MediaServer._tempFilePath = Path.Combine(Path.GetTempPath(), "cast_temp_" + Guid.NewGuid().ToString() + ".mp4");
                        Console.WriteLine($"[Info] Initializing background transcoding to: {MediaServer._tempFilePath}");

                        var startInfo = new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = $"-loglevel quiet -nostats -i \"{MediaServer._sourceFilePath}\" -map 0:v:0 -map 0:a:0? -sn -c:v copy -c:a aac -ac 2 -movflags frag_keyframe+empty_moov -y \"{MediaServer._tempFilePath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        MediaServer._transcodeProcess = Process.Start(startInfo);

                        // Pre-buffer 15MB to prevent TV startup timeouts
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("[Info] Pre-buffering transcode cache to ensure smooth TV startup. Please wait...");
                        Console.ResetColor();

                        long targetPrebuffer = Math.Min(15000000L, MediaServer._sourceFileSize);
                        while (true)
                        {
                            if (File.Exists(MediaServer._tempFilePath))
                            {
                                long currentSize = new FileInfo(MediaServer._tempFilePath).Length;
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

                            if (MediaServer._transcodeProcess != null && MediaServer._transcodeProcess.HasExited)
                            {
                                break;
                            }
                            await Task.Delay(250);
                        }
                        Console.WriteLine("\n[Info] Pre-buffering complete! Launching cast...");
                    }
                }

                // Auto-select first device if not specified
                if (targetIp == null && ccIndex == null)
                {
                    ccIndex = 1;
                    Console.WriteLine("[Info] Local file mode: Auto-selecting first discovered device (--cc 1).");
                }
            }

            string mimeType = MediaServer._hlsDir != null 
                ? "application/x-mpegURL" 
                : (MediaServer._sourceFilePath != null 
                    ? (MediaServer._isAudioOnly 
                        ? MediaServer.GetAudioMimeType(MediaServer._tempFilePath ?? MediaServer._sourceFilePath) 
                        : (MediaServer._sourceFilePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? "video/mp2t" : "video/mp4")) 
                    : MediaServer.GetMimeType(imageBytes));

            string extension = mimeType switch
            {
                "application/x-mpegURL" => "m3u8",
                "video/mp2t" => "ts",
                "video/mp4" => "mp4",
                "audio/mp4" => "mp4",
                "audio/mpeg" => "mp3",
                "audio/aac" => "aac",
                "audio/wav" => "wav",
                "audio/flac" => "flac",
                "audio/ogg" => "ogg",
                "video/webm" => "webm",
                "image/jpeg" => "jpg",
                "image/gif" => "gif",
                _ => "png"
            };

            List<IReceiver> receivers = new List<IReceiver>();
            if (!previewMode)
            {
                Console.WriteLine("[Info] Scanning network interfaces and probing Living Room TV (192.168.50.109)...");
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
                        bool isTvOpen = await DeviceDiscovery.CheckPortAsync(targetTvIp, 8009, 1500);
                        if (isTvOpen)
                        {
                            string name = await DeviceDiscovery.GetEurekaDeviceNameAsync(targetTvIp);
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

                if (scanMode)
                {
                    if (receivers.Count > 0)
                    {
                        Console.WriteLine("\nDiscovered devices:");
                        for (int i = 0; i < receivers.Count; i++)
                        {
                            Console.WriteLine($"  [{i + 1}] {receivers[i].FriendlyName} ({receivers[i].IPEndPoint})");
                        }
                    }
                    else
                    {
                        Console.WriteLine("\nNo devices discovered.");
                    }
                    return;
                }

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
            }
            else
            {
                if (scanMode)
                {
                    Console.WriteLine("[Info] Scan option requested in preview mode. Skipping discovery.");
                    return;
                }
            }

            if (targetIp != null)
            {
                Console.WriteLine($"\n[Info] Target TV IP: {targetIp}");
            }

            // Find local active IP to host our web server
            string localIp = "127.0.0.1";
            if (targetIp != null)
            {
                localIp = NetworkUtils.GetLocalIpAddress(targetIp);
                Console.WriteLine($"[Info] Local IP Address: {localIp}");
            }

            // Start simple HTTP Server to host the blue image or transcode segment
            HttpListener? listener = null;
            int port = 0;
            try
            {
                (listener, port) = MediaServer.StartHttpServer(localIp);
                Console.WriteLine($"[Info] Web server started at http://{localIp}:{port}/media.png");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] Failed to start local web server: {ex.Message}");
                Console.ResetColor();
                return;
            }

            if (targetIp == null)
            {
                if (previewMode)
                {
                    // Run the HTTP server request processing in the background
                    _ = Task.Run(() => MediaServer.RunHttpServerAsync(listener, imageBytes));

                    string previewExtension = extension;
                    string previewUri = $"http://127.0.0.1:{port}/media.{previewExtension}";
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("\n========================================================");
                    Console.WriteLine(" [Success] Local preview started!");
                    Console.WriteLine($" Preview URL: {previewUri}");
                    Console.WriteLine("========================================================");
                    Console.ResetColor();

                    Console.WriteLine("[Info] Launching local preview window via ffplay...");
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = "ffplay",
                            Arguments = $"-loglevel quiet -nostats -autoexit \"{previewUri}\"",
                            UseShellExecute = false,
                            CreateNoWindow = false
                        };
                        using (var previewProcess = Process.Start(startInfo))
                        {
                            if (previewProcess != null)
                            {
                                previewProcess.WaitForExit();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Failed to launch local preview: {ex.Message}");
                    }
                    return;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[Error] No target IP address specified.");
                    Console.ResetColor();
                    return;
                }
            }

            // Run the HTTP server request processing in the background
            _ = Task.Run(() => MediaServer.RunHttpServerAsync(listener, imageBytes));

            // Launch local preview window if requested alongside casting
            if (previewMode)
            {
                string previewExtension = extension;
                string previewUri = $"http://127.0.0.1:{port}/media.{previewExtension}";
                Console.WriteLine($"[Info] Launching local preview window via ffplay: {previewUri}");
                _ = Task.Run(() =>
                {
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = "ffplay",
                            Arguments = $"-loglevel quiet -nostats -autoexit \"{previewUri}\"",
                            UseShellExecute = false,
                            CreateNoWindow = false
                        };
                        using (var previewProcess = Process.Start(startInfo))
                        {
                            if (previewProcess != null)
                            {
                                previewProcess.WaitForExit();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Warning] Failed to launch local preview: {ex.Message}");
                    }
                });
            }

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



                string imageUri = $"http://{localIp}:{port}/media.{extension}?t={DateTime.UtcNow.Ticks}";
                Console.WriteLine($"[Info] Casting media URL: {imageUri} (MIME: {mimeType})");

                StreamType streamType = StreamType.None;
                if (isVideo || isAudio)
                {
                    streamType = (MediaServer._sourceFilePath != null && !MediaServer._isLiveMode) ? StreamType.Buffered : StreamType.Live;
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
                if (MediaServer._sourceFilePath != null)
                {
                    Console.WriteLine($" The TV should now play local {(isVideo ? "video" : "audio")}: {Path.GetFileName(MediaServer._sourceFilePath)}");
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
                                if ((isVideo || isAudio) && status.PlayerState == "IDLE" && (status.IdleReason == "FINISHED" || status.IdleReason == "ERROR"))
                                {
                                    Console.WriteLine($"\n[Info] {(isVideo ? "Video" : "Audio")} playback finished ({status.IdleReason}). Auto-exiting...");
                                    tcs.TrySetResult(true);
                                }

                                DateTime now = DateTime.UtcNow;
                                // HLS mode: the receiver seeks natively within the playlist; no interception needed.
                                if (MediaServer._hlsDir == null && status.PlayerState == "PLAYING" && !isFirstStatus && lastPlayerState == "PLAYING")
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
                if (MediaServer._transcodeProcess != null)
                {
                    try { MediaServer._transcodeProcess.Kill(); } catch { }
                }
                if (MediaServer._tempFilePath != null && MediaServer._tempFilePath.StartsWith(Path.GetTempPath()) && File.Exists(MediaServer._tempFilePath))
                {
                    try { File.Delete(MediaServer._tempFilePath); } catch { }
                }
                if (MediaServer._renderedHtmlPath != null && File.Exists(MediaServer._renderedHtmlPath))
                {
                    try { File.Delete(MediaServer._renderedHtmlPath); } catch { }
                }
                if (MediaServer._hlsDir != null && Directory.Exists(MediaServer._hlsDir))
                {
                    try { Directory.Delete(MediaServer._hlsDir, true); } catch { }
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
            }
        }

        static void ShowHelp()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Usage: cast-local [options] [filepath] [IP address]\n");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Options:");
            Console.WriteLine("  -h, --help       Show this help message.");
            Console.WriteLine("  --scan           Scan the local network for Cast devices, list them, and exit.");
            Console.WriteLine("  --cc <index>     Auto-select a discovered device by its 1-based index.");
            Console.WriteLine("  --size <bytes>   Specify the estimated total size of the stream in bytes.");
            Console.WriteLine("  --live           Use legacy live transcoding mode (fragmented MP4) instead of HLS.");
            Console.WriteLine("  -r, --resolution Specify viewport resolution for SVG/HTML/URL rendering (e.g. 1920x1080, default 1920x1080).");
            Console.WriteLine("  -d, --duration   Specify playtime duration for SVG/HTML/URL rendering (e.g. 30s or 30, default 30s).");
            Console.WriteLine("  -y, --delay      Specify load wait delay before recording frames (e.g. 3s or 3, default 0s).");
            Console.WriteLine("  -p, --preview    Launch a local ffplay preview window showing exactly what will be cast.\n");
            Console.WriteLine("Examples:");
            Console.WriteLine("  cast-local --scan");
            Console.WriteLine("  cast-local \"/path/to/video.mkv\"");
            Console.WriteLine("  cast-local \"/path/to/video.mkv\" 192.168.1.50");
            Console.WriteLine("  cast-local \"/path/to/video.mkv\" --cc 1");
            Console.ResetColor();
        }
    }
}
