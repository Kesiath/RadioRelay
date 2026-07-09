using System;

namespace RadioRelay.Shared.Audio.Effects
{
    /// DCS-SRS "$type": "saturation" -- drives the signal by a
    /// linear gain multiplier, then soft-clips (tanh-based, not a hard
    /// clip) anything above thresholdDb, giving the gritty analog-overdrive
    /// edge characteristic of a radio's transmit chain.
    public class SaturationEffect : IAudioEffect
    {
        private readonly float _gain;
        private readonly float _thresholdLinear;

        public SaturationEffect(float gain, float thresholdDb)
        {
            _gain = gain;
            _thresholdLinear = Math.Clamp(DbToLinear(thresholdDb), 0.01f, 0.99f);
        }

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float s = samples[i] * _gain;
                float mag = Math.Abs(s);

                if (mag > _thresholdLinear)
                {
                    float sign = Math.Sign(s);
                    float over = mag - _thresholdLinear;
                    float headroom = 1f - _thresholdLinear;
                    float compressed = _thresholdLinear + headroom * (float)Math.Tanh(over / headroom);
                    s = sign * compressed;
                }

                samples[i] = Math.Clamp(s, -1f, 1f);
            }
        }

        private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);
    }
}
