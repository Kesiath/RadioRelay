using System;

namespace RadioRelay.Shared.Audio.Effects
{
    /// <summary>
    /// Applies frequency-independent gain in decibels.
    /// </summary>
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
