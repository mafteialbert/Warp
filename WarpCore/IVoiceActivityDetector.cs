namespace WarpCore
{
    public interface IVoiceActivityDetector
    {
        /// <summary>
        /// Detects voice activity in the audio file. The audio file must be raw PCM 32FLE with a sample rate of 16kHz
        /// </summary
        /// <param name="audioFile">The audio file</param>
        /// <returns>Result of voice activity detection</returns>
        public Task<VADResult> DetectVoiceActivity(FileInfo audioFile);
        public Task<VADResult> DetectVoiceActivity(Stream audioStream);
    }
}
