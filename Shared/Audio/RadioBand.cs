namespace RadioRelay.Shared.Audio
{
    /// <summary>
    /// Selects band-specific DSP and receiver-noise characteristics.
    /// </summary>
    public enum RadioBand
    {
        HF,   // About 2-30 MHz.
        VHF,  // About 30-300 MHz.
        UHF   // About 300 MHz and above.
    }

    public static class RadioBandExtensions
    {
        public static RadioBand FromFrequencyMHz(float frequencyMHz)
        {
            if (frequencyMHz < 30f) return RadioBand.HF;
            if (frequencyMHz < 300f) return RadioBand.VHF;
            return RadioBand.UHF;
        }
    }
}
