namespace RadioRelay.Shared.Audio.Effects
{
    /// <summary>
    /// Processes normalized mono samples in place as one stage of an effect chain.
    /// </summary>
    public interface IAudioEffect
    {
        void Process(float[] samples);

        /// <summary>
        /// Clears state at a transmission boundary.
        /// </summary>
        void Reset() { }
    }
}
