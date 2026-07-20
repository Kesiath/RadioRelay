namespace RadioRelay.Shared.Audio.Effects
{
    /// <summary>
    /// Second-order high-pass; removes content below frequencyHz.
    /// </summary>
    public class HighPassEffect : IAudioEffect
    {
        private readonly BiQuadFilter _filter;

        public HighPassEffect(int sampleRate, float frequencyHz, double q = 0.707)
        {
            _filter = BiQuadFilter.HighPass(sampleRate, frequencyHz, q);
        }

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = _filter.Process(samples[i]);
        }

        public void Reset() => _filter.Reset();
    }

    /// <summary>
    /// Second-order low-pass; removes content above frequencyHz.
    /// </summary>
    public class LowPassEffect : IAudioEffect
    {
        private readonly BiQuadFilter _filter;

        public LowPassEffect(int sampleRate, float frequencyHz, double q = 0.707)
        {
            _filter = BiQuadFilter.LowPass(sampleRate, frequencyHz, q);
        }

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = _filter.Process(samples[i]);
        }

        public void Reset() => _filter.Reset();
    }

    /// <summary>
    /// Applies parametric gain around a center frequency.
    /// </summary>
    public class PeakEffect : IAudioEffect
    {
        private readonly BiQuadFilter _filter;

        public PeakEffect(int sampleRate, float frequencyHz, double q, float gainDb)
        {
            _filter = BiQuadFilter.Peaking(sampleRate, frequencyHz, q, gainDb);
        }

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = _filter.Process(samples[i]);
        }

        public void Reset() => _filter.Reset();
    }

    /// <summary>
    /// Applies a 6 dB-per-octave high-pass at natural radio edges.
    /// </summary>
    public sealed class FirstOrderHighPassEffect : IAudioEffect
    {
        private readonly FirstOrderFilter _filter;

        public FirstOrderHighPassEffect(int sampleRate, float frequencyHz) =>
            _filter = FirstOrderFilter.HighPass(sampleRate, frequencyHz);

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = _filter.Process(samples[i]);
        }

        public void Reset() => _filter.Reset();
    }

    /// <summary>
    /// Applies a 6 dB-per-octave low-pass at natural radio edges.
    /// </summary>
    public sealed class FirstOrderLowPassEffect : IAudioEffect
    {
        private readonly FirstOrderFilter _filter;

        public FirstOrderLowPassEffect(int sampleRate, float frequencyHz) =>
            _filter = FirstOrderFilter.LowPass(sampleRate, frequencyHz);

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = _filter.Process(samples[i]);
        }

        public void Reset() => _filter.Reset();
    }
}
