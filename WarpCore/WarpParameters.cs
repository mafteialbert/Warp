namespace WarpCore
{
    /// <summary>
    /// Parameters for the WarpProcessor when warping videos
    /// </summary>
    public class WarpParameters
    {
        /// <summary>
        /// Function that maps a probability to a speed
        /// </summary>
        public required Func<double, double> SpeedFunction;


        public static WarpParameters SimpleThreshold(double threshold, double loudSpeed, double silentSpeed)
            => new() { SpeedFunction = (probability) => (probability >= threshold) ? loudSpeed : silentSpeed };

    }
}
