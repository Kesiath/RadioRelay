using System;

namespace RadioRelay.Client.AudioEngineNs
{
    /// 
    /// Stateful destructive collision processor for received audio. It does
    /// not play a recorded squelch bed; it damages the captured samples with
    /// multipath-like cancellation, rapid flutter/chopping, scratch bursts,
    /// and broadband hiss so listeners hear the received transmission break
    /// up while two stations overlap.
    /// 
    internal sealed class RadioCollisionDestructionModel
    {
        private readonly int _sampleRate;
        private readonly float[] _delayLine;
        private int _delayIndex;
        private int _sampleClock;
        private uint _noiseState = 0x6D2B79F5;
        private float _lastNoise;

        public RadioCollisionDestructionModel(int sampleRate)
        {
            _sampleRate = sampleRate;
            _delayLine = new float[Math.Max(1, sampleRate / 175)]; // ~5.7ms multipath delay
        }

        public void Reset()
        {
            Array.Clear(_delayLine, 0, _delayLine.Length);
            _delayIndex = 0;
            _sampleClock = 0;
            _noiseState = 0x6D2B79F5;
            _lastNoise = 0f;
        }

        public void Process(float[] frame, bool active)
        {
            if (!active) return;

            for (int i = 0; i < frame.Length; i++)
            {
                float t = _sampleClock / (float)_sampleRate;
                float dry = frame[i];
                float delayed = _delayLine[_delayIndex];

                // A drifting cancellation path gives the "multipath/nulling"
                // sensation: the captured voice folds against a delayed copy
                // instead of simply having an unrelated noise loop mixed over it.
                float cancellationDepth = 0.42f + 0.18f * MathF.Sin(Tau * 2.7f * t);
                float cancelled = (dry * (0.62f - cancellationDepth)) - (delayed * cancellationDepth);

                // Fast flutter/chop makes overlapping stations sound like they
                // are cutting each other apart instead of just getting quieter.
                float flutter = 0.58f + 0.42f * MathF.Sin(Tau * 19.0f * t + 0.7f * MathF.Sin(Tau * 3.1f * t));
                float chop = MathF.Sin(Tau * 31.0f * t) > -0.20f ? 1.0f : 0.16f;

                float noise = NextNoise();
                float scratch = noise - (_lastNoise * 0.82f);
                _lastNoise = noise;

                float whoosh = 0.55f + 0.45f * MathF.Sin(Tau * 5.4f * t + 1.3f * MathF.Sin(Tau * 0.9f * t));
                float hiss = scratch * (0.035f + 0.030f * whoosh);

                // Occasional sharp scratch ticks add a tearing edge without
                // making the static dominate the captured voice.
                if ((_noiseState & 0xFFu) > 246u)
                    hiss += MathF.Sign(noise) * 0.055f;

                frame[i] = Math.Clamp((cancelled * flutter * chop) + hiss, -1f, 1f);

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
