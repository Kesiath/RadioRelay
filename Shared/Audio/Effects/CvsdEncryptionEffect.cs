using System;

namespace RadioRelay.Shared.Audio.Effects
{
    /// <summary>
    /// Adds mild CVSD-style coloration without providing transport security.
    /// </summary>
    public class CvsdEncryptionEffect : IAudioEffect
    {
        private const float WetMix = 0.20f;
        private const float MinimumStep = 0.004f;
        private const float MaximumStep = 0.20f;
        private float _reference;
        private float _step = 0.018f;
        private int _recentBits;

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float dry = samples[i];
                int bit = dry >= _reference ? 1 : 0;
                _recentBits = ((_recentBits << 1) | bit) & 0b111;

                if (_recentBits is 0 or 0b111)
                    _step = Math.Min(MaximumStep, _step * 1.22f);
                else
                    _step = Math.Max(MinimumStep, _step * 0.94f);

                _reference = Math.Clamp(
                    _reference + (bit == 1 ? _step : -_step),
                    -1f,
                    1f);
                samples[i] = Math.Clamp(
                    dry * (1f - WetMix) + _reference * WetMix,
                    -1f,
                    1f);
            }
        }

        public void Reset()
        {
            _reference = 0f;
            _step = 0.018f;
            _recentBits = 0;
        }
    }
}
