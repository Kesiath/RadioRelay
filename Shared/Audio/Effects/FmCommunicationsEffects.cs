using System;

namespace RadioRelay.Shared.Audio.Effects
{
    /// <summary>
    /// Applies matched communications-FM pre-emphasis or de-emphasis normalized at 1 kHz.
    /// </summary>
    public sealed class FmEmphasisEffect : IAudioEffect
    {
        public const float LowerCornerHz = 300f;
        public const float UpperCornerHz = 3000f;
        public const float ReferenceFrequencyHz = 1000f;

        private readonly float _b0;
        private readonly float _b1;
        private readonly float _a1;
        private float _previousInput;
        private float _previousOutput;

        public FmEmphasisEffect(int sampleRate, bool preEmphasis)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            float zeroHz = preEmphasis ? LowerCornerHz : UpperCornerHz;
            float poleHz = preEmphasis ? UpperCornerHz : LowerCornerHz;
            float bilinearK = 2f * sampleRate;
            float numerator0 = 1f + bilinearK / (2f * MathF.PI * zeroHz);
            float numerator1 = 1f - bilinearK / (2f * MathF.PI * zeroHz);
            float denominator0 = 1f + bilinearK / (2f * MathF.PI * poleHz);
            float denominator1 = 1f - bilinearK / (2f * MathF.PI * poleHz);

            float rawB0 = numerator0 / denominator0;
            float rawB1 = numerator1 / denominator0;
            _a1 = denominator1 / denominator0;

            float omega = 2f * MathF.PI * ReferenceFrequencyHz / sampleRate;
            float cos = MathF.Cos(omega);
            float sin = MathF.Sin(omega);
            float numeratorReal = rawB0 + rawB1 * cos;
            float numeratorImaginary = -rawB1 * sin;
            float denominatorReal = 1f + _a1 * cos;
            float denominatorImaginary = -_a1 * sin;
            float magnitude = MathF.Sqrt(
                (numeratorReal * numeratorReal + numeratorImaginary * numeratorImaginary) /
                (denominatorReal * denominatorReal + denominatorImaginary * denominatorImaginary));
            float normalization = 1f / Math.Max(1e-6f, magnitude);

            _b0 = rawB0 * normalization;
            _b1 = rawB1 * normalization;
        }

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];
                float output = _b0 * input + _b1 * _previousInput - _a1 * _previousOutput;
                samples[i] = Math.Clamp(output, -4f, 4f);
                _previousInput = input;
                _previousOutput = output;
            }
        }

        public void Reset()
        {
            _previousInput = 0f;
            _previousOutput = 0f;
        }
    }

    /// <summary>
    /// Maps audio to normalized 25 kHz communications-FM deviation with hard limiting.
    /// </summary>
    public sealed class FmDeviationLimiterEffect : IAudioEffect
    {
        public const float MaximumDeviationHz = 5000f;
        public const float ReferenceToneDbov = -28f;
        public const float ReferenceDeviationFraction = 0.60f;

        private static readonly float InputToNormalizedDeviation =
            ReferenceDeviationFraction / MathF.Pow(10f, ReferenceToneDbov / 20f);

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float normalizedDeviation = samples[i] * InputToNormalizedDeviation;
                samples[i] = Math.Clamp(normalizedDeviation, -1f, 1f);
            }
        }
    }

    /// <summary>
    /// Applies seeded transmitter gain, response tilt, and asymmetry for one PTT.
    /// </summary>
    public sealed class TransmissionHardwareVariationEffect : IAudioEffect
    {
        private const uint DefaultSeed = 0x7F4A7C15u;
        private readonly FirstOrderFilter _bodyFilter;
        private float _gainLinear = 1f;
        private float _tilt;
        private float _asymmetry;

        public TransmissionHardwareVariationEffect(int sampleRate)
        {
            _bodyFilter = FirstOrderFilter.LowPass(sampleRate, 1150f);
            Reset(DefaultSeed);
        }

        public void Reset(uint transmissionSeed)
        {
            uint state = transmissionSeed == 0 ? DefaultSeed : transmissionSeed;
            float gainDb = NextSigned(ref state) * 0.65f;
            _gainLinear = MathF.Pow(10f, gainDb / 20f);
            _tilt = NextSigned(ref state) * 0.055f;
            _asymmetry = NextSigned(ref state) * 0.025f;
            _bodyFilter.Reset();
        }

        public void Reset() => Reset(DefaultSeed);

        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];
                float body = _bodyFilter.Process(input);
                float presence = input - body;
                float tilted = (body * (1f - _tilt) + presence * (1f + _tilt)) * _gainLinear;
                float asymmetric = tilted >= 0f
                    ? tilted * (1f + _asymmetry)
                    : tilted * (1f - _asymmetry);
                samples[i] = Math.Clamp(asymmetric, -1f, 1f);
            }
        }

        private static float NextSigned(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return ((state & 0x00FFFFFFu) / 8388607.5f) - 1f;
        }
    }
}
