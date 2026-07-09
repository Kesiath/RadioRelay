namespace RadioRelay.Shared.Audio
{
    /// Which real-world radio band a frequency falls into. Drives
    /// both the tunable DSP effect chain AND which recorded background
    /// static loop plays underneath received audio -- so tuning a radio
    /// from 251 MHz to 8 MHz should audibly change its whole character, the
    /// same way switching between a UHF and an HF set would in real life.
    public enum RadioBand
    {
        HF,   // ~2-30 MHz
        VHF,  // ~30-300 MHz
        UHF   // ~300 MHz and up
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
