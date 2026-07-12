using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CastBlueScreen
{
    public static class MediaServer
    {
        // Base64 for a 1x1 solid blue PNG image.
        public static readonly byte[] BluePngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
        );

        public static long? _contentSize = null;
        public static string? _sourceFilePath = null;
        public static long _sourceFileSize = 0;
        public static double _sourceDuration = 0;
        public static string? _tempFilePath = null;
        public static string? _hlsDir = null;
        public static Process? _transcodeProcess = null;
        public static double _currentSeekSeconds = 0;
        public static readonly SemaphoreSlim _transcodeLock = new SemaphoreSlim(1, 1);
        public static bool _isLiveMode = false;
        public static bool _isAudioOnly = false;
        public static bool _isTranscoding = false;
        public static string? _renderedHtmlPath = null;

        public static (HttpListener listener, int port) StartHttpServer(string localIp)
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

        public static async Task RunHttpServerAsync(HttpListener listener, byte[] imageBytes)
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
                            string mimeType = _sourceFilePath != null 
                                ? (_isAudioOnly 
                                    ? GetAudioMimeType(_tempFilePath ?? _sourceFilePath) 
                                    : (_sourceFilePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? "video/mp2t" : "video/mp4")) 
                                : GetMimeType(imageBytes);

                            if (_hlsDir != null)
                            {
                                await ServeHlsAsync(request, response);
                            }
                            else if (_sourceFilePath != null)
                            {
                                long totalSize = _isLiveMode ? 999999999999L : _sourceFileSize;
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
                                 if (_isTranscoding && !string.IsNullOrEmpty(seekParam) && double.TryParse(seekParam, out double querySeek))
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

                                             string mapArgs = _isAudioOnly 
                                                 ? "-map 0:a:0? -vn -c:a libmp3lame -q:a 2" 
                                                 : "-map 0:v:0 -map 0:a:0? -sn -c:v copy -c:a aac -ac 2";

                                              var startInfo = new ProcessStartInfo
                                              {
                                                  FileName = "ffmpeg",
                                                  Arguments = $"-loglevel warning -nostats -ss {querySeek:F2} -i \"{_sourceFilePath}\" {mapArgs} " +
                                                              (_isAudioOnly ? "" : "-movflags frag_keyframe+empty_moov") + 
                                                              $" -y \"{_tempFilePath}\"",
                                                  RedirectStandardOutput = true,
                                                  RedirectStandardError = true,
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

        private static async Task ServeHlsAsync(HttpListenerRequest request, HttpListenerResponse response)
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

        public static string GetMimeType(byte[] bytes)
        {
            return GetMimeType(bytes, bytes.Length);
        }

        public static string GetMimeType(byte[] bytes, int length)
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

        public static string GetAudioMimeType(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".mp3" => "audio/mpeg",
                ".aac" => "audio/aac",
                ".wav" => "audio/wav",
                ".flac" => "audio/flac",
                ".ogg" => "audio/ogg",
                ".m4a" => "audio/mp4",
                _ => "audio/mp4" // default fallback
            };
        }
    }
}
