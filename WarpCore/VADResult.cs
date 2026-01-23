namespace WarpCore
{
    /// <summary>
    /// Represents the result from a IVoiceActivityDetector
    /// </summary>
    /// <param name="probabilities">Probabilities for each window to contain speech</param>
    /// <param name="windowSize">The size of the audio windows that were evaluated</param>
    public class VADResult(float[] probabilities, long windowSize)
    {
        /// <summary>
        /// Probabilities for each window to contain speech
        /// </summary>
        public float[] Probabilities = probabilities;
        
        /// <summary>
        /// The size of the audio windows
        /// </summary>
        public long WindowSize = windowSize;
    }
}
