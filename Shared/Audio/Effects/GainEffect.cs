using System;

namespace RadioRelay.Shared.Audio.Effects
{
    /// DCS-SRS "$type": "gain" -- a plain, non-frequency-dependent
    /// level change in dB. Usually used as final makeup gain at the end of
    /// a chain after filtering/compression have reduced overall level.
    public class GainEffect : IAudioEffect
    {
        private readonly float _linearGain;

        public GainEffect(float gainDb)
        {
            _linearGain = (float)Math.Pow(10, gainDb / 20.0);
        }

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = Math.Clamp(samples[i] * _linearGain, -1f, 1f);
        }
    }
}
