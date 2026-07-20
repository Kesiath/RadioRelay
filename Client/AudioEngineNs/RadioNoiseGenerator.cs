using System;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Generates seeded receiver noise from procedural texture, recorded band
    /// noise, and frequency-dependent coloration.
    /// </summary>
    internal sealed class RadioNoiseGenerator
    {
        private uint _state;
        private bool _hasGaussianSpare;
        private float _gaussianSpare;
        private float _lowNoise;
        private float _midNoise;
        private float _authoredNoise;
        private float _textureWander;
        private float _gritResonator1;
        private float _gritResonator2;
        private uint _recordedOffsetSeed;
        private int _recordedLoopPosition = -1;

        private const float SampleRate = AudioEngine.SampleRate;

        public RadioNoiseGenerator(uint seed = 0xA341316Cu)
        {
            Reset(seed);
        }

        public void AddTo(
            float[] frame,
            RadioEffectProfile profile)
        {
            float representativeFrequency = profile.Band switch
            {
                RadioBand.HF => 15f,
                RadioBand.UHF => 650f,
                _ => 165f
            };
            AddTo(frame, profile, representativeFrequency);
        }

        public void AddTo(
            float[] frame,
            RadioEffectProfile profile,
            float frequencyMHz)
        {
            float gain = profile.NoiseGainLinear;
            float authoredGain = profile.AuthoredNoiseGainLinear;
            if (gain <= 0f && authoredGain <= 0f) return;

            NoiseContour contour = NoiseContour.For(profile.Band, frequencyMHz);
            float lowPole = PoleForCutoff(contour.LowCutoffHz);
            float midPole = PoleForCutoff(contour.MidCutoffHz);
            float authoredPole = PoleForCutoff(contour.AuthoredCutoffHz);
            float lowNormalization = LowPassNormalization(lowPole);
            float midNormalization = LowPassNormalization(midPole);
            float resonatorCoefficient =
                2f * contour.GritDecay * MathF.Cos(2f * MathF.PI * contour.GritFrequencyHz / SampleRate);
            float resonatorDecaySquared = contour.GritDecay * contour.GritDecay;

            var authoredLoop = SoundLibrary.GetBandNoiseLoop(profile.Band);
            if (_recordedLoopPosition < 0 && authoredLoop.Length > 0)
                _recordedLoopPosition = (int)(_recordedOffsetSeed % (uint)authoredLoop.Length);
            else if (authoredLoop.Length > 0 && _recordedLoopPosition >= authoredLoop.Length)
                _recordedLoopPosition %= authoredLoop.Length;

            for (int i = 0; i < frame.Length; i++)
            {
                float white = NextGaussian();
                _lowNoise = lowPole * _lowNoise + (1f - lowPole) * white;
                _midNoise = midPole * _midNoise + (1f - midPole) * white;

                // Add sparse low-mid resonances for granular receiver growl.
                float gritExcitation = 0f;
                if (NextUnit() < contour.GritEventsPerSecond / SampleRate)
                    gritExcitation = MathF.CopySign(1.1f + MathF.Abs(white) * 0.35f, NextUnit() - 0.5f);
                float grit = resonatorCoefficient * _gritResonator1
                    - resonatorDecaySquared * _gritResonator2
                    + gritExcitation;
                _gritResonator2 = _gritResonator1;
                _gritResonator1 = grit;

                _textureWander = _textureWander * 0.9985f + white * 0.0015f;
                float movement = 1f + Math.Clamp(_textureWander * 4f, -0.16f, 0.16f);
                float noise = (
                    _lowNoise * lowNormalization * contour.LowMix
                    + _midNoise * midNormalization * contour.MidMix
                    + white * contour.WhiteMix
                    + MathF.Tanh(grit * 0.20f) * contour.GritMix) * movement;

                float authored = 0f;
                if (authoredLoop.Length > 0)
                {
                    float authoredSample = authoredLoop[_recordedLoopPosition];
                    _authoredNoise = authoredPole * _authoredNoise + (1f - authoredPole) * authoredSample;
                    authored = _authoredNoise * authoredGain;
                    _recordedLoopPosition++;
                    if (_recordedLoopPosition >= authoredLoop.Length)
                        _recordedLoopPosition = 0;
                }

                frame[i] = Math.Clamp(frame[i] + noise * gain + authored, -1f, 1f);
            }
        }

        public void Reset(uint seed = 0xA341316Cu)
        {
            _state = seed == 0 ? 0xA341316Cu : seed;
            _recordedOffsetSeed = _state;
            _recordedLoopPosition = -1;
            _hasGaussianSpare = false;
            _gaussianSpare = 0f;
            _lowNoise = 0f;
            _midNoise = 0f;
            _authoredNoise = 0f;
            _textureWander = 0f;
            _gritResonator1 = 0f;
            _gritResonator2 = 0f;
        }

        private static float PoleForCutoff(float cutoffHz) =>
            MathF.Exp(-2f * MathF.PI * cutoffHz / SampleRate);

        private static float LowPassNormalization(float pole) =>
            MathF.Sqrt((1f + pole) / Math.Max(1e-6f, 1f - pole));

        private readonly struct NoiseContour
        {
            public float LowCutoffHz { get; init; }
            public float MidCutoffHz { get; init; }
            public float AuthoredCutoffHz { get; init; }
            public float GritFrequencyHz { get; init; }
            public float GritDecay { get; init; }
            public float GritEventsPerSecond { get; init; }
            public float LowMix { get; init; }
            public float MidMix { get; init; }
            public float WhiteMix { get; init; }
            public float GritMix { get; init; }

            public static NoiseContour For(RadioBand band, float frequencyMHz)
            {
                float position = band switch
                {
                    RadioBand.HF => Math.Clamp((frequencyMHz - 2f) / 28f, 0f, 1f),
                    RadioBand.VHF => Math.Clamp((frequencyMHz - 30f) / 270f, 0f, 1f),
                    _ => Math.Clamp((frequencyMHz - 300f) / 699f, 0f, 1f)
                };

                return band switch
                {
                    RadioBand.HF => new NoiseContour
                    {
                        LowCutoffHz = Lerp(360f, 500f, position),
                        MidCutoffHz = Lerp(850f, 1200f, position),
                        AuthoredCutoffHz = Lerp(1050f, 1450f, position),
                        GritFrequencyHz = Lerp(390f, 540f, position),
                        GritDecay = 0.989f,
                        GritEventsPerSecond = Lerp(15f, 11f, position),
                        LowMix = 0.61f,
                        MidMix = 0.25f,
                        WhiteMix = 0.035f,
                        GritMix = 0.34f
                    },
                    RadioBand.UHF => new NoiseContour
                    {
                        LowCutoffHz = Lerp(650f, 880f, position),
                        MidCutoffHz = Lerp(1450f, 2100f, position),
                        AuthoredCutoffHz = Lerp(1800f, 2450f, position),
                        GritFrequencyHz = Lerp(650f, 850f, position),
                        GritDecay = 0.983f,
                        GritEventsPerSecond = Lerp(7f, 4f, position),
                        LowMix = 0.44f,
                        MidMix = 0.39f,
                        WhiteMix = 0.08f,
                        GritMix = 0.20f
                    },
                    _ => new NoiseContour
                    {
                        LowCutoffHz = Lerp(500f, 700f, position),
                        MidCutoffHz = Lerp(1100f, 1700f, position),
                        AuthoredCutoffHz = Lerp(1400f, 2050f, position),
                        GritFrequencyHz = Lerp(520f, 720f, position),
                        GritDecay = 0.986f,
                        GritEventsPerSecond = Lerp(10f, 7f, position),
                        LowMix = 0.52f,
                        MidMix = 0.33f,
                        WhiteMix = 0.055f,
                        GritMix = 0.27f
                    }
                };
            }

            private static float Lerp(float from, float to, float amount) =>
                from + (to - from) * amount;
        }

        private float NextGaussian()
        {
            if (_hasGaussianSpare)
            {
                _hasGaussianSpare = false;
                return _gaussianSpare;
            }

            float u1 = Math.Max(1e-7f, NextUnit());
            float u2 = NextUnit();
            float radius = MathF.Sqrt(-2f * MathF.Log(u1));
            float angle = 2f * MathF.PI * u2;
            _gaussianSpare = radius * MathF.Sin(angle);
            _hasGaussianSpare = true;
            return radius * MathF.Cos(angle);
        }

        private float NextUnit() => (NextUInt() & 0x00FFFFFFu) / 16777216f;

        private uint NextUInt()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }
    }
}
