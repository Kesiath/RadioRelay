using System;

namespace RadioRelay.Client.AudioEngineNs
{
    /// Band-limited, stateful 3x interpolator from the 16 kHz stereo receive
    /// frame to 48 kHz stereo PCM. It is deliberately push-driven: every
    /// 20 ms input frame produces exactly one 20 ms output frame, so an
    /// output callback can never manufacture silence by reading beyond the
    /// currently available input block.
    internal sealed class LocalPassthroughOutputConverter
    {
        private const int InterpolationFactor = 3;
        private const int TapCount = 33;
        private static readonly double[] Coefficients = CreateCoefficients();

        private readonly double[] _leftHistory = new double[TapCount];
        private readonly double[] _rightHistory = new double[TapCount];
        private int _writePosition;

        internal const double AlgorithmicLatencyMilliseconds =
            ((TapCount - 1) / 2.0) * 1000.0 / AudioEngine.PassthroughOutputSampleRate;

        public byte[] Convert(short[] stereo16Khz)
        {
            if ((stereo16Khz.Length & 1) != 0)
                throw new ArgumentException("Expected interleaved stereo samples.", nameof(stereo16Khz));

            int sourceFrames = stereo16Khz.Length / 2;
            var pcm48Khz = new byte[sourceFrames * InterpolationFactor * 8];
            int byteOffset = 0;

            for (int frame = 0; frame < sourceFrames; frame++)
            {
                Push(stereo16Khz[frame * 2], stereo16Khz[frame * 2 + 1]);
                WriteFilteredStereo(pcm48Khz, ref byteOffset);

                Push(0, 0);
                WriteFilteredStereo(pcm48Khz, ref byteOffset);

                Push(0, 0);
                WriteFilteredStereo(pcm48Khz, ref byteOffset);
            }

            return pcm48Khz;
        }

        public void Reset()
        {
            Array.Clear(_leftHistory, 0, _leftHistory.Length);
            Array.Clear(_rightHistory, 0, _rightHistory.Length);
            _writePosition = 0;
        }

        private void Push(double left, double right)
        {
            _leftHistory[_writePosition] = left;
            _rightHistory[_writePosition] = right;
            _writePosition++;
            if (_writePosition == TapCount) _writePosition = 0;
        }

        private void WriteFilteredStereo(byte[] destination, ref int offset)
        {
            double left = 0;
            double right = 0;
            int historyPosition = _writePosition - 1;
            if (historyPosition < 0) historyPosition = TapCount - 1;

            for (int tap = 0; tap < TapCount; tap++)
            {
                double coefficient = Coefficients[tap];
                left += _leftHistory[historyPosition] * coefficient;
                right += _rightHistory[historyPosition] * coefficient;
                historyPosition--;
                if (historyPosition < 0) historyPosition = TapCount - 1;
            }

            WriteSample(destination, ref offset, ToFloat(left));
            WriteSample(destination, ref offset, ToFloat(right));
        }

        private static float ToFloat(double sample) =>
            (float)Math.Clamp(sample / 32768.0, -1.0, 1.0);

        private static void WriteSample(byte[] destination, ref int offset, float sample)
        {
            int bits = BitConverter.SingleToInt32Bits(sample);
            destination[offset++] = unchecked((byte)bits);
            destination[offset++] = unchecked((byte)(bits >> 8));
            destination[offset++] = unchecked((byte)(bits >> 16));
            destination[offset++] = unchecked((byte)(bits >> 24));
        }

        private static double[] CreateCoefficients()
        {
            var coefficients = new double[TapCount];
            int center = (TapCount - 1) / 2;
            const double cutoffCyclesPerOutputSample = 1.0 / 6.0; // 8 kHz at 48 kHz
            double sum = 0;

            for (int tap = 0; tap < TapCount; tap++)
            {
                int distance = tap - center;
                double sinc = distance == 0
                    ? 2 * cutoffCyclesPerOutputSample
                    : Math.Sin(2 * Math.PI * cutoffCyclesPerOutputSample * distance) /
                        (Math.PI * distance);
                double blackman = 0.42
                    - 0.5 * Math.Cos(2 * Math.PI * tap / (TapCount - 1))
                    + 0.08 * Math.Cos(4 * Math.PI * tap / (TapCount - 1));
                coefficients[tap] = sinc * blackman;
                sum += coefficients[tap];
            }

            // Zero insertion reduces DC gain by the interpolation factor.
            double scale = InterpolationFactor / sum;
            for (int tap = 0; tap < coefficients.Length; tap++)
                coefficients[tap] *= scale;

            return coefficients;
        }
    }
}
