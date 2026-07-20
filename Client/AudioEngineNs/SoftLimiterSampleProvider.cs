using System;
using NAudio.Wave;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Applies zero-lookahead soft limiting to overlapping output peaks.
    /// </summary>
    internal sealed class SoftLimiterSampleProvider : ISampleProvider
    {
        private const float Knee = 0.88f;
        private readonly ISampleProvider _source;

        public SoftLimiterSampleProvider(ISampleProvider source) => _source = source;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            for (int i = 0; i < read; i++)
                buffer[offset + i] = Limit(buffer[offset + i]);
            return read;
        }

        internal static float Limit(float sample)
        {
            float magnitude = MathF.Abs(sample);
            if (magnitude <= Knee) return sample;
            float normalized = (magnitude - Knee) / (1f - Knee);
            float limited = Knee + (1f - Knee) * MathF.Tanh(normalized);
            return MathF.CopySign(Math.Min(1f, limited), sample);
        }
    }
}
