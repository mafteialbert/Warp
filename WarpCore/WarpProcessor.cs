using System;
using System.Diagnostics;

namespace WarpCore
{
    public class WarpProcessor(string resourcePath, string suffix="")
    {
        private readonly FF ff = new(resourcePath, suffix);


        public async Task<FileInfo> ProcessVideoAsync(FileInfo videoFile, IVoiceActivityDetector activityDetector, WarpParameters parameters, Action<string>? progress = null)
        {
            string processingTempFolder = Path.Combine(Path.GetTempPath(), "WarpCore_" + Guid.NewGuid());
            progress?.Invoke($"Creating temporary files at: {processingTempFolder}");
            Directory.CreateDirectory(processingTempFolder);
            var warpedVideo = new FileInfo(Path.Combine(processingTempFolder, "video.warped.mp4"));
            var timemapAudio = new FileInfo(Path.Combine(processingTempFolder, "timemap.audio.txt"));
            var timemapVideo = new FileInfo(Path.Combine(processingTempFolder, "timemap.video.raw"));
            var audio = new FileInfo(Path.Combine(processingTempFolder, "audio.mp3"));
            var warpedAudio = new FileInfo(Path.Combine(processingTempFolder, "audio.warped.mp3"));

            var sampleRateTask = ff.FFprobe_GetSampleRateAsync(videoFile.FullName).ContinueWith(t => {
                progress?.Invoke("Got sample rate.");
                return t.Result;  
            });

            var channelCountTask = ff.FFprobe_GetChannelCountAsync(videoFile.FullName).ContinueWith(t => {
                progress?.Invoke("Got channel count.");
                return t.Result;
            });

            var videoPTSTask = ff.FFmpeg_GetVideoPTSAsync(videoFile.FullName).ContinueWith(t => {
                progress?.Invoke("Got video PTS.");
                return t.Result;
            });
            var videoTimebaseTask = ff.FFmpeg_GetVideoTimebaseAsync(videoFile.FullName).ContinueWith(t => {
                progress?.Invoke("Got video timebase.");
                return t.Result;
            });

            var detectVoiceActivityTask = activityDetector.DetectVoiceActivity(ff.FFmpeg_StreamRawAudioAsync(videoFile.FullName, 16000, 1)).ContinueWith(t => {
                progress?.Invoke("Got voice activity.");
                return t.Result;
            });

            var totalSamplesTask = ff.FFprobe_GetTotalSamplesAsync(videoFile.FullName).ContinueWith(t => {
                progress?.Invoke("Got total samples.");
                return t.Result;
            });

            int sampleRate = await sampleRateTask;
            int channelCount = await channelCountTask;
            var extractAudioTask = ff.FFmpeg_ExtractAudioAsync(videoFile.FullName, audio.FullName, sampleRate, channelCount).ContinueWith(t => {
                progress?.Invoke("Extracted audio.");
            });

            await extractAudioTask;
            long totalSamples = await totalSamplesTask;
            VADResult result = await detectVoiceActivityTask;
            long[] pts = await videoPTSTask;
            (int timebaseDen, int timebaseNum) = await videoTimebaseTask;

            // Number of windows
            int numWindows = result.Probabilities.Length;
            long windowSizeResampled = result.WindowSize;
            var audioIn = new List<long>();
            var audioOut = new List<double>();
            double cumulativeOut = 0.0;
            using (var writer = new StreamWriter(timemapAudio.FullName))
            {
                double scale = sampleRate / 16000.0;
                

                for (int i = 0; i < numWindows; i++)
                {
                    long inputStart = (long)Math.Round(i * windowSizeResampled * scale);
                    long inputEnd = (long)Math.Round((i + 1) * windowSizeResampled * scale);


                    double speed = parameters.SpeedFunction.Invoke(result.Probabilities[i]);

                    double inputDuration = inputEnd - inputStart;
                    double warpedSamples = inputDuration / speed;

                    // Store in-memory mapping
                    audioIn.Add(inputStart);
                    audioOut.Add(cumulativeOut);

                    writer.WriteLine($"{inputStart} {Math.Round(cumulativeOut)}");

                    cumulativeOut += warpedSamples;
                }

                // Final sample
                audioIn.Add(totalSamples);
                audioOut.Add(cumulativeOut);

                writer.WriteLine($"{totalSamples} {Math.Round(cumulativeOut)}");
            }

            progress?.Invoke("Processed audio timemap.");

            const long AV_TIME_BASE = 1_000_000;

            using (var fs = new FileStream(timemapVideo.FullName, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                double lastAudioOut = audioOut[^1];
                long lastAudioIn = audioIn[^1];

                int cachedIdx = 0;   // We start searching from index 0
                int maxIdx = audioIn.Count - 1;

                for (int i = 0; i < pts.Length; i++)
                {
                    // Convert PTS → seconds → sample index
                    double ptsSeconds = pts[i] * (double)timebaseDen / timebaseNum;
                    double ptsSamples = ptsSeconds * sampleRate;

                    // ------------------------------------------------------------
                    // 🚀 FAST SEARCH: Move cachedIdx forward as long as needed
                    // ------------------------------------------------------------
                    while (cachedIdx < maxIdx && audioIn[cachedIdx] < ptsSamples)
                        cachedIdx++;

                    double warpedSamples;

                    if (cachedIdx == 0)
                    {
                        // Before first window
                        warpedSamples = audioOut[0];
                    }
                    else if (cachedIdx >= audioIn.Count)
                    {
                        // Past last window
                        warpedSamples = lastAudioOut;
                    }
                    else
                    {
                        // Interpolate between cachedIdx-1 and cachedIdx
                        long in0 = audioIn[cachedIdx - 1];
                        long in1 = audioIn[cachedIdx];
                        double out0 = audioOut[cachedIdx - 1];
                        double out1 = audioOut[cachedIdx];

                        double denom = in1 - in0;
                        double t = denom > 0 ? (ptsSamples - in0) / denom : 0;

                        warpedSamples = out0 + t * (out1 - out0);
                    }

                    // Convert to AV_TIME_BASE
                    long tIn = (long)Math.Round(
                        pts[i] * (AV_TIME_BASE * (double)timebaseDen / timebaseNum));

                    long tOut = (long)Math.Round(
                        (warpedSamples / sampleRate) * AV_TIME_BASE);

                    bw.Write(tIn);
                    bw.Write(tOut);
                }
            }

            progress?.Invoke("Processed video timemap.");
            await ff.Rubberband_WarpAsync(audio.FullName, cumulativeOut / sampleRate, timemapAudio.FullName, warpedAudio.FullName);
            progress?.Invoke("Rubberband finished.");
            await ff.FFmpeg_WarpAsync(videoFile.FullName, timemapVideo, warpedAudio.FullName, warpedVideo.FullName);
            progress?.Invoke("FFmpeg finished.");
            return warpedVideo;
        }
    }
}
