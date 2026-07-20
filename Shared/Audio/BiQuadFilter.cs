using System;

namespace RadioRelay.Shared.Audio
{
    /// <summary>
    /// Implements RBJ biquad filters for radio passband shaping.
    /// </summary>
    public class BiQuadFilter
    {
        private double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        public static BiQuadFilter HighPass(float sampleRate, float cutoffHz, double q = 0.707)
        {
            var f = new BiQuadFilter();
            f.SetHighPass(sampleRate, cutoffHz, q);
            return f;
        }

        public static BiQuadFilter LowPass(float sampleRate, float cutoffHz, double q = 0.707)
        {
            var f = new BiQuadFilter();
            f.SetLowPass(sampleRate, cutoffHz, q);
            return f;
        }

        /// <summary>
        /// Creates a constant skirt-gain band-pass filter centered at <paramref name="centerHz"/>.
        /// </summary>
        public static BiQuadFilter BandPass(float sampleRate, float centerHz, double q = 0.9)
        {
            var f = new BiQuadFilter();
            f.SetBandPass(sampleRate, centerHz, q);
            return f;
        }

        /// <summary>
        /// Creates a peaking EQ centered at <paramref name="centerHz"/>.
        /// </summary>
        public static BiQuadFilter Peaking(float sampleRate, float centerHz, double q, float gainDb)
        {
            var f = new BiQuadFilter();
            f.SetPeaking(sampleRate, centerHz, q, gainDb);
            return f;
        }

        public void SetPeaking(float sampleRate, float centerHz, double q, float gainDb)
        {
            double a = Math.Pow(10, gainDb / 40.0);
            double w0 = 2 * Math.PI * centerHz / sampleRate;
            double alpha = Math.Sin(w0) / (2 * q);
            double cosw0 = Math.Cos(w0);

            double b0 = 1 + alpha * a;
            double b1 = -2 * cosw0;
            double b2 = 1 - alpha * a;
            double a0 = 1 + alpha / a;
            double a1 = -2 * cosw0;
            double a2 = 1 - alpha / a;

            SetCoefficients(b0, b1, b2, a0, a1, a2);
        }

        public void SetBandPass(float sampleRate, float centerHz, double q)
        {
            double w0 = 2 * Math.PI * centerHz / sampleRate;
            double alpha = Math.Sin(w0) / (2 * q);
            double cosw0 = Math.Cos(w0);

            double b0 = alpha;
            double b1 = 0;
            double b2 = -alpha;
            double a0 = 1 + alpha;
            double a1 = -2 * cosw0;
            double a2 = 1 - alpha;

            SetCoefficients(b0, b1, b2, a0, a1, a2);
        }

        public void SetHighPass(float sampleRate, float cutoffHz, double q)
        {
            double w0 = 2 * Math.PI * cutoffHz / sampleRate;
            double alpha = Math.Sin(w0) / (2 * q);
            double cosw0 = Math.Cos(w0);

            double b0 = (1 + cosw0) / 2;
            double b1 = -(1 + cosw0);
            double b2 = (1 + cosw0) / 2;
            double a0 = 1 + alpha;
            double a1 = -2 * cosw0;
            double a2 = 1 - alpha;

            SetCoefficients(b0, b1, b2, a0, a1, a2);
        }

        public void SetLowPass(float sampleRate, float cutoffHz, double q)
        {
            double w0 = 2 * Math.PI * cutoffHz / sampleRate;
            double alpha = Math.Sin(w0) / (2 * q);
            double cosw0 = Math.Cos(w0);

            double b0 = (1 - cosw0) / 2;
            double b1 = 1 - cosw0;
            double b2 = (1 - cosw0) / 2;
            double a0 = 1 + alpha;
            double a1 = -2 * cosw0;
            double a2 = 1 - alpha;

            SetCoefficients(b0, b1, b2, a0, a1, a2);
        }

        private void SetCoefficients(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
            _a1 = a1 / a0; _a2 = a2 / a0;
        }

        public float Process(float x0)
        {
            double y0 = _b0 * x0 + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x0;
            _y2 = _y1; _y1 = y0;
            return (float)y0;
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;
    }
}
