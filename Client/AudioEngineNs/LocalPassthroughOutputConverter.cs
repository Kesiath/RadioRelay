using System;
using NAudio.Dsp;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Converts 16 kHz stereo radio frames directly to the selected output rate.
    /// </summary>
    internal sealed class LocalPassthroughOutputConverter
    {
        private const int Channels = 2;
        private WdlResampler _resampler = null!;
        private long _inputFramesConverted;
        private long _outputFramesEmitted;

        public LocalPassthroughOutputConverter(
            int outputSampleRate = AudioEngine.PassthroughOutputSampleRate)
        {
            SetOutputSampleRate(outputSampleRate);
        }

        public int OutputSampleRate { get; private set; }

        public byte[] Convert(short[] stereo16Khz)
        {
            if ((stereo16Khz.Length & 1) != 0)
                throw new ArgumentException("Expected interleaved stereo samples.", nameof(stereo16Khz));
            if (stereo16Khz.Length == 0) return Array.Empty<byte>();

            int sourceFrames = stereo16Khz.Length / Channels;
            int requiredInputFrames = _resampler.ResamplePrepare(
                sourceFrames,
                Channels,
                out var input,
                out int inputOffset);
            if (requiredInputFrames != sourceFrames)
                throw new InvalidOperationException("The passthrough resampler did not accept the complete frame.");

            for (int index = 0; index < stereo16Khz.Length; index++)
                input[inputOffset + index] = stereo16Khz[index] / 32768f;

            _inputFramesConverted += sourceFrames;
            long expectedOutputFrames = _inputFramesConverted * OutputSampleRate / AudioEngine.SampleRate;
            int requiredOutputFrames = checked((int)(expectedOutputFrames - _outputFramesEmitted));
            int maximumOutputFrames = requiredOutputFrames + 16;
            var output = new float[maximumOutputFrames * Channels];
            int outputFrames = _resampler.ResampleOut(
                output,
                0,
                sourceFrames,
                maximumOutputFrames,
                Channels);

            int emittedFrames = Math.Max(requiredOutputFrames, outputFrames);
            int leadingSilentFrames = emittedFrames - outputFrames;
            var pcm = new byte[emittedFrames * Channels * sizeof(float)];
            Buffer.BlockCopy(
                output,
                0,
                pcm,
                leadingSilentFrames * Channels * sizeof(float),
                outputFrames * Channels * sizeof(float));
            _outputFramesEmitted += emittedFrames;
            return pcm;
        }

        public void SetOutputSampleRate(int outputSampleRate)
        {
            if (outputSampleRate < 8_000 || outputSampleRate > 192_000)
                throw new ArgumentOutOfRangeException(nameof(outputSampleRate));

            OutputSampleRate = outputSampleRate;
            Reset();
        }

        public void Reset()
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(wantInputDriven: true);
            _resampler.SetRates(AudioEngine.SampleRate, OutputSampleRate);
            _inputFramesConverted = 0;
            _outputFramesEmitted = 0;
        }
    }
}
