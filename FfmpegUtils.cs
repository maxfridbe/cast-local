using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using PuppeteerSharp;

namespace CastBlueScreen
{
    public static class FfmpegUtils
    {
        public static async Task<double> GetVideoDurationAsync(string filePath)
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

        public static async Task<bool> HasVideoAsync(string filePath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -select_streams v -show_entries stream=codec_type -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
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
                        return !string.IsNullOrWhiteSpace(output) && output.Contains("video");
                    }
                }
            }
            catch { }
            return false;
        }

        public static async Task RenderWebPageToMp4Async(string inputFilePath, string outputMp4Path, int width, int height, double durationSeconds, double delaySeconds = 0)
        {
            Console.WriteLine("[Info] Downloading/verifying headless Chromium binary...");
            var browserFetcher = new BrowserFetcher();
            await browserFetcher.DownloadAsync();

            Console.WriteLine("[Info] Starting headless Chromium browser...");
            using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox" }
            });

            Console.WriteLine("[Info] Loading input URL/file into Chromium viewport...");
            using var page = await browser.NewPageAsync();

            // Set viewport to specified resolution
            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = width,
                Height = height
            });

            // Load page content based on URL/file type
            if (inputFilePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                inputFilePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await page.GoToAsync(inputFilePath, new NavigationOptions
                {
                    WaitUntil = new[] { WaitUntilNavigation.Networkidle2 }
                });
            }
            else if (inputFilePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                string svgContent = File.ReadAllText(inputFilePath);
                string htmlContent = $@"
<!DOCTYPE html>
<html>
<head>
    <style>
        html, body {{
            margin: 0;
            padding: 0;
            width: 100%;
            height: 100%;
            overflow: hidden;
            background-color: black;
            display: flex;
            justify-content: center;
            align-items: center;
        }}
        svg {{
            width: 100%;
            height: 100%;
            max-width: 100%;
            max-height: 100%;
        }}
    </style>
</head>
<body>
    {svgContent}
</body>
</html>";
                await page.SetContentAsync(htmlContent);
            }
            else
            {
                string fileUrl = new Uri(Path.GetFullPath(inputFilePath)).AbsoluteUri;
                await page.GoToAsync(fileUrl, new NavigationOptions
                {
                    WaitUntil = new[] { WaitUntilNavigation.Networkidle2 }
                });

                await page.AddStyleTagAsync(new AddTagOptions
                {
                    Content = "html, body { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background-color: black; }"
                });
            }

            if (delaySeconds > 0)
            {
                Console.WriteLine($"[Info] Waiting {delaySeconds:F1} seconds for page to initialize...");
                await Task.Delay((int)(delaySeconds * 1000));
            }

            string formatFlags = outputMp4Path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                ? "-f mpegts"
                : (double.IsInfinity(durationSeconds) ? "-movflags frag_keyframe+empty_moov+default_base_moof -g 30 -keyint_min 30 -sc_threshold 0" : "");
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-loglevel warning -nostats -y -f image2pipe -framerate 30 -i - -c:v libx264 -pix_fmt yuv420p -b:v 4000k {formatFlags} \"{outputMp4Path}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var ffmpegProcess = Process.Start(startInfo);
            if (ffmpegProcess == null)
            {
                throw new Exception("Failed to start FFmpeg worker process.");
            }

            // Keep reference of live encoder in MediaServer if running progressively
            if (double.IsInfinity(durationSeconds))
            {
                MediaServer._transcodeProcess = ffmpegProcess;
            }

            using var stdin = ffmpegProcess.StandardInput.BaseStream;
            int fps = 30;
            int totalFrames = double.IsInfinity(durationSeconds) ? int.MaxValue : (int)(durationSeconds * fps);
            double frameDelayMs = 1000.0 / fps;

            // Try to detect and pause SMIL animations
            bool hasSmil = await page.EvaluateFunctionAsync<bool>(@"() => {
                const svg = document.querySelector('svg');
                if (svg && typeof svg.setCurrentTime === 'function') {
                    try {
                        svg.pauseAnimations();
                        return true;
                    } catch { }
                }
                return false;
            }");

            Console.WriteLine(hasSmil 
                ? "[Info] SMIL animations detected. Enabling frame-accurate rendering..." 
                : "[Info] Real-time rendering active...");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool isInfinite = double.IsInfinity(durationSeconds);
            int loopLimit = isInfinite ? int.MaxValue : totalFrames;

            for (int frame = 0; frame < loopLimit; frame++)
            {
                if (ffmpegProcess.HasExited)
                {
                    break;
                }

                double currentTime = frame / (double)fps;

                if (hasSmil)
                {
                    await page.EvaluateFunctionAsync("function(t) { const svg = document.querySelector('svg'); if (svg) svg.setCurrentTime(t); }", currentTime);
                }
                else
                {
                    double targetElapsedMs = frame * frameDelayMs;
                    double actualElapsedMs = stopwatch.ElapsedMilliseconds;
                    if (targetElapsedMs > actualElapsedMs)
                    {
                        await Task.Delay((int)(targetElapsedMs - actualElapsedMs));
                    }
                }

                // Capture viewport frame screenshot as PNG
                byte[] screenshot = await page.ScreenshotDataAsync(new ScreenshotOptions
                {
                    Type = ScreenshotType.Png
                });

                // Write PNG bytes to FFmpeg stdin pipe
                await stdin.WriteAsync(screenshot, 0, screenshot.Length);

                // Print rendering progress
                if (isInfinite)
                {
                    Console.Write($"\r[Rendering] Live frame: {frame + 1}...");
                }
                else
                {
                    double progress = (frame + 1) * 100.0 / totalFrames;
                    Console.Write($"\r[Rendering] Progress: {progress:F1}% ({frame + 1}/{totalFrames} frames)...");
                }
            }

            Console.WriteLine(isInfinite 
                ? "\n[Rendering] Live render stopped." 
                : "\n[Rendering] Frame sequence completed. Finalizing MP4 encode...");
            stdin.Close();
            await ffmpegProcess.WaitForExitAsync();
        }
    }
}
