using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace WarpCore
{
    /// <summary>
    /// Utility class to wrap FFmpeg, FFprobe and Rubberband
    /// </summary>
    /// <param name="resourceFolderPath">Folder with executables</param>
    public class FF(string resourceFolderPath, string suffix = "")
    {
        private readonly string FFmpegPath = Path.Combine(resourceFolderPath, "ffmpeg" + suffix);
        private readonly string FFprobePath = Path.Combine(resourceFolderPath, "ffprobe" + suffix);
        private readonly string RubberbandPath = Path.Combine(resourceFolderPath, "rubberband" + suffix);

        #region FFprobe

        public async Task<long> FFprobe_GetTotalSamplesAsync(string videoFile)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FFprobePath,
                Arguments =
                    $"-v error -show_entries format=duration:stream=sample_rate -of csv=p=0 \"{videoFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(startInfo)
                ?? throw new Exception("Process failed starting.");

            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            // Expected ffprobe output: "12.345678,48000"
            var parts = output.Trim().Split(["\r", "\n", "\r\n"], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2)
                throw new Exception($"ffprobe returned unexpected output: '{output}'");

            int sampleRate = int.Parse(parts[0], CultureInfo.InvariantCulture);
            double durationSeconds = double.Parse(parts[1], CultureInfo.InvariantCulture);

            return (long)(durationSeconds * sampleRate);
        }

        public async Task<int> FFprobe_GetChannelCountAsync(string file)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFprobePath,
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", "a:0",
                    "-show_entries", "stream=channels",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    file
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi) ?? throw new Exception("Process failed starting");
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            output = output.Trim();

            if (string.IsNullOrWhiteSpace(output))
                throw new Exception("ffprobe returned no channel information.");

            if (!int.TryParse(output, out int channels))
                throw new Exception($"Invalid ffprobe channel value: '{output}'");

            return channels;
        }

        public async Task<int> FFprobe_GetSampleRateAsync(string audioFile)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFprobePath,
                ArgumentList =
                {
                    "-select_streams", "a:0",
                    "-show_entries", "stream=sample_rate",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    audioFile
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi) ?? throw new Exception("Process failed starting");
            string output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();

            if (int.TryParse(output, out int sampleRate))
                return sampleRate;
            return 0;
        }



        #endregion


        #region FFmpeg
        public async Task FFmpeg_ExtractRawAudioAsync(string videoFile, string audioFile, int sampleRate, int channels)
        {

            var psi = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                ArgumentList =
                {
                    "-i", videoFile,
                    "-vn",
                    "-f", "f32le",
                    "-ar", sampleRate.ToString(),
                    "-c:a", "pcm_f32le",
                    "-ac", channels.ToString(),
                    audioFile,
                },
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new Exception("Process failed starting");
            await process.WaitForExitAsync();
        }

        public Stream FFmpeg_StreamRawAudioAsync(string videoFile, int sampleRate, int channels)
        {

            var psi = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                ArgumentList =
                {
                    "-i", videoFile,
                    "-vn",
                    "-f", "f32le",
                    "-ar", sampleRate.ToString(),
                    "-c:a", "pcm_f32le",
                    "-ac", channels.ToString(),
                    "-",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new Exception("Process failed starting");
            return process.StandardOutput.BaseStream;
        }
        public async Task FFmpeg_ExtractAudioAsync(string videoFile, string audioFile, int sampleRate, int channels, int bitrate = 192)
        {

            var psi = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                ArgumentList =
                {
                    "-i", videoFile,
                    "-vn",
                    "-ar", sampleRate.ToString(),
                    "-ac", channels.ToString(),
                    "-b:a", $"{bitrate}k",
                    audioFile,
                },
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new Exception("Process failed starting");
            await process.WaitForExitAsync();
        }
        public async Task<long[]> FFmpeg_GetVideoPTSAsync(string videoFile)
        {
            // Use ffprobe to get PTS and timebase
            var psiPTS = new ProcessStartInfo
            {
                FileName = FFprobePath,
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "frame=pts",
                    "-of", "csv=p=0",
                    videoFile
                },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var processPTS = Process.Start(psiPTS) ?? throw new Exception("Process failed starting");
            string output = (await processPTS.StandardOutput.ReadToEndAsync()).Trim().Replace(",", "");
            await processPTS.WaitForExitAsync();
            var ptsStrings = output.Split(["\r", "\n", "\r\n"], StringSplitOptions.RemoveEmptyEntries);
            long[] pts = [.. ptsStrings.Select(s => long.Parse(s.Trim()))];
            return pts;
        }

        public async Task<(int tbNum, int tbDen)> FFmpeg_GetVideoTimebaseAsync(string videoFile)
        {
            // Get video timebase
            var psiTimebase = new ProcessStartInfo
            {
                FileName = FFprobePath,
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=time_base",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    videoFile
                },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var processTimebase = Process.Start(psiTimebase) ?? throw new Exception("Process failed starting");
            string tbOutput = (await processTimebase.StandardOutput.ReadToEndAsync()).Trim();
            await processTimebase.WaitForExitAsync();

            string[] tbParts = tbOutput.Split('/');
            int tbNum = int.Parse(tbParts[0]);
            int tbDen = int.Parse(tbParts[1]);

            return (tbNum, tbDen);
        }


        public async Task FFmpeg_WarpAsync(string videoFile, FileInfo timemapVideo, string audioWarped, string outputVideo)
        {


            var psi = new ProcessStartInfo
            {
                FileName = FFmpegPath,
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                WorkingDirectory = timemapVideo.Directory.FullName,
#pragma warning restore CS8602 // Dereference of a possibly null reference.
                ArgumentList =
                {
                    "-y",
                    "-i", videoFile,
                    "-i", audioWarped,

                    // video warp filter
                    "-filter_complex", $"[v0]warp=timemap={timemapVideo.Name}[outv]",

                    // outputs
                    "-map", "[outv]",
                    "-map", "1:a",

                    "-c:v", "h264_nvenc",
                    "-preset", "p1",
                    "-c:a", "aac",
                    "-b:a", "192k",
                    outputVideo
                },
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new Exception("Process failed starting");
            await process.WaitForExitAsync();
        }

        #endregion


        #region Rubberband

        public async Task Rubberband_WarpAsync(string inputAudioFile, double duration, string timemapAudioFile, string outputAudioFile)
        {
            var psi = new ProcessStartInfo
            {
                FileName = RubberbandPath,
                ArgumentList =
                {
                    "--timemap", timemapAudioFile,
                    "-D", duration.ToString(),
                    inputAudioFile, outputAudioFile,
                },
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new Exception("Process failed starting");
            await process.WaitForExitAsync();
        }

        #endregion
    }
}