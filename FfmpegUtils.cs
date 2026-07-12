using System;
using System.Diagnostics;
using System.Threading.Tasks;

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
    }
}
