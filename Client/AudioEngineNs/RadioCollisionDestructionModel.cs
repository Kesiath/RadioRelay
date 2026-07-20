using System;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Damages overlapping received audio with cancellation, flutter, scratches, and hiss.
    /// </summary>
    internal sealed class RadioCollisionDestructionModel
    {
        private readonly int _sampleRate;
        private readonly float[] _delayLine;
        private int _delayIndex;
        private int _sampleClock;
        private uint _noiseState = 0x6D2B79F5;
        private float _lastNoise;
        private readonly FirstOrderFilter _hfOutputFilter;
        private readonly FirstOrderFilter _vhfOutputFilter;
        private readonly FirstOrderFilter _uhfOutputFilter;

        public RadioCollisionDestructionModel(int sampleRate)
        {
            _sampleRate = sampleRate;
            _delayLine = new float[Math.Max(1, sampleRate / 175)]; // About 5.7 ms multipath delay.
            _hfOutputFilter = FirstOrderFilter.LowPass(sampleRate, 2700f);
            _vhfOutputFilter = FirstOrderFilter.LowPass(sampleRate, 3100f);
            _uhfOutputFilter = FirstOrderFilter.LowPass(sampleRate, 3750f);
        }

        public void Reset()
        {
            Array.Clear(_delayLine, 0, _delayLine.Length);
            _delayIndex = 0;
            _sampleClock = 0;
            _noiseState = 0x6D2B79F5;
            _lastNoise = 0f;
            _hfOutputFilter.Reset();
            _vhfOutputFilter.Reset();
            _uhfOutputFilter.Reset();
        }

        public void Process(float[] frame, bool active, RadioBand band = RadioBand.VHF)
        {
            if (!active) return;

            var outputFilter = band switch
            {
                RadioBand.HF => _hfOutputFilter,
                RadioBand.UHF => _uhfOutputFilter,
                _ => _vhfOutputFilter
            };

            for (int i = 0; i < frame.Length; i++)
            {
                float t = _sampleClock / (float)_sampleRate;
                float dry = frame[i];
                float delayed = _delayLine[_delayIndex];

                // Fold the signal against a drifting delayed path.
                float cancellationDepth = 0.42f + 0.18f * MathF.Sin(Tau * 2.7f * t);
                float cancelled = (dry * (0.62f - cancellationDepth)) - (delayed * cancellationDepth);

                // Add rapid flutter and chopping between overlapping stations.
                float flutter = 0.58f + 0.42f * MathF.Sin(Tau * 19.0f * t + 0.7f * MathF.Sin(Tau * 3.1f * t));
                float chop = MathF.Sin(Tau * 31.0f * t) > -0.20f ? 1.0f : 0.16f;

                float noise = NextNoise();
                float scratch = noise - (_lastNoise * 0.82f);
                _lastNoise = noise;

                float whoosh = 0.55f + 0.45f * MathF.Sin(Tau * 5.4f * t + 1.3f * MathF.Sin(Tau * 0.9f * t));
                float hiss = scratch * (0.035f + 0.030f * whoosh);

                // Add sparse scratch transients without masking the voice.
                if ((_noiseState & 0xFFu) > 246u)
                    hiss += MathF.Sign(noise) * 0.055f;

                float damaged = (cancelled * flutter * chop) + hiss;
                frame[i] = Math.Clamp(outputFilter.Process(damaged), -1f, 1f);

                _delayLine[_delayIndex] = dry;
                _delayIndex++;
                if (_delayIndex >= _delayLine.Length) _delayIndex = 0;
                _sampleClock++;
            }
        }

        private float NextNoise()
        {
            _noiseState ^= _noiseState << 13;
            _noiseState ^= _noiseState >> 17;
            _noiseState ^= _noiseState << 5;
            return ((_noiseState & 0x00FFFFFFu) / 8388607.5f) - 1f;
        }

        private const float Tau = MathF.PI * 2f;
    }
}
