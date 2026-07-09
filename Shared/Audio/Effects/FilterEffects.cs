namespace RadioRelay.Shared.Audio.Effects
{
    /// DCS-SRS "$type": "highpass" -- removes content below frequencyHz.
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
    }

    /// DCS-SRS "$type": "lowpass" -- removes content above frequencyHz.
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
    }

    /// DCS-SRS "$type": "peak" -- boosts/cuts a band around
    /// frequencyHz by gainDb, leaving the rest of the spectrum alone. This is
    /// what gives narrowband radio voice its characteristic "presence"/edge.
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
    }
}
