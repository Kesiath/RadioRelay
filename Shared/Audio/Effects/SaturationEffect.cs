using System;

namespace RadioRelay.Shared.Audio.Effects
{
    /// <summary>
    /// Applies zero-latency soft-knee saturation with dB drive and threshold controls.
    /// </summary>
    public class SaturationEffect : IAudioEffect
    {
        private readonly float _driveLinear;
        private readonly float _thresholdLinear;
        private float _previousDriven;

        public SaturationEffect(float gainDb, float thresholdDb)
        {
            _driveLinear = DbToLinear(gainDb);
            _thresholdLinear = Math.Clamp(DbToLinear(thresholdDb), 0.01f, 0.99f);
        }

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float driven = samples[i] * _driveLinear;
                float step = (driven - _previousDriven) / 3f;

                // Average three sub-samples to reduce fold-back aliasing without buffering.
                float shaped = (
                    Shape(_previousDriven + step) +
                    Shape(_previousDriven + step * 2f) +
                    Shape(driven)) / 3f;

                samples[i] = Math.Clamp(shaped, -1f, 1f);
                _previousDriven = driven;
            }
        }

        public void Reset() => _previousDriven = 0f;

        private float Shape(float sample)
        {
            float magnitude = Math.Abs(sample);
            if (magnitude <= _thresholdLinear) return sample;

            float over = (magnitude - _thresholdLinear) / Math.Max(0.001f, 1f - _thresholdLinear);
            float headroom = 1f - _thresholdLinear;
            float compressed = _thresholdLinear + headroom * (float)Math.Tanh(over);
            return MathF.CopySign(compressed, sample);
        }

        private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);
    }
}
