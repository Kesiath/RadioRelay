using System;

namespace RadioRelay.Shared.Audio
{
    /// <summary>
    /// Implements a stateful one-pole low-pass or high-pass filter.
    /// </summary>
    public sealed class FirstOrderFilter
    {
        private readonly bool _highPass;
        private readonly float _pole;
        private float _previousInput;
        private float _previousOutput;

        private FirstOrderFilter(float sampleRate, float cutoffHz, bool highPass)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            cutoffHz = Math.Clamp(cutoffHz, 1f, sampleRate * 0.49f);
            _pole = MathF.Exp(-2f * MathF.PI * cutoffHz / sampleRate);
            _highPass = highPass;
        }

        public static FirstOrderFilter LowPass(float sampleRate, float cutoffHz) =>
            new(sampleRate, cutoffHz, highPass: false);

        public static FirstOrderFilter HighPass(float sampleRate, float cutoffHz) =>
            new(sampleRate, cutoffHz, highPass: true);

        public float Process(float input)
        {
            float output;
            if (_highPass)
                output = _pole * (_previousOutput + input - _previousInput);
            else
                output = (1f - _pole) * input + _pole * _previousOutput;

            _previousInput = input;
            _previousOutput = output;
            return output;
        }

        public void Reset()
        {
            _previousInput = 0f;
            _previousOutput = 0f;
        }
    }
}
