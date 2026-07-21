using System;
using System.Collections.Generic;
using WdlResampler = NAudio.Dsp.WdlResampler;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Downmixes and resamples application loopback audio for the shared TX chain.
    /// </summary>
    internal sealed class ApplicationAmbienceProcessor
    {
        internal const int CaptureSampleRate = 48000;
        internal const int CaptureChannels = 2;
        internal const int MaximumBufferedMilliseconds = 120;
        internal const int MaximumLiveBacklogMilliseconds = 60;

        private const int FadeInMilliseconds = 15;

        private readonly object _gate = new();
        private readonly Queue<float> _samples = new();
        private WdlResampler _resampler = CreateResampler();
        private int _fadeInSamplesRemaining;

        public void WritePcm16Stereo(byte[] pcm)
        {
            if (pcm == null) throw new ArgumentNullException(nameof(pcm));
            int sourceFrames = pcm.Length / (CaptureChannels * sizeof(short));
            if (sourceFrames == 0) return;

            lock (_gate)
            {
                int acceptedFrames = _resampler.ResamplePrepare(
                    sourceFrames,
                    1,
                    out var input,
                    out int inputOffset);
                if (acceptedFrames != sourceFrames)
                    throw new InvalidOperationException("The ambience resampler did not accept the complete capture packet.");

                for (int frame = 0; frame < sourceFrames; frame++)
                {
                    int byteOffset = frame * CaptureChannels * sizeof(short);
                    short left = BitConverter.ToInt16(pcm, byteOffset);
                    short right = BitConverter.ToInt16(pcm, byteOffset + sizeof(short));
                    input[inputOffset + frame] = (left + right) / 65536f;
                }

                int maximumOutputFrames = checked(
                    (int)Math.Ceiling(sourceFrames * AudioEngine.SampleRate / (double)CaptureSampleRate) + 32);
                var output = new float[maximumOutputFrames];
                int outputFrames = _resampler.ResampleOut(
                    output,
                    0,
                    sourceFrames,
                    maximumOutputFrames,
                    1);

                for (int index = 0; index < outputFrames; index++)
                    _samples.Enqueue(Math.Clamp(output[index], -1f, 1f));

                int maximumSamples = AudioEngine.SampleRate * MaximumBufferedMilliseconds / 1000;
                while (_samples.Count > maximumSamples)
                    _samples.Dequeue();
            }
        }

        public float[] ReadSamples(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var output = new float[count];
            if (count == 0) return output;

            lock (_gate)
            {
                int maximumBacklog = AudioEngine.SampleRate * MaximumLiveBacklogMilliseconds / 1000;
                while (_samples.Count > maximumBacklog)
                    _samples.Dequeue();

                int fadeLength = Math.Max(1, AudioEngine.SampleRate * FadeInMilliseconds / 1000);
                for (int index = 0; index < count && _samples.Count > 0; index++)
                {
                    float fade = _fadeInSamplesRemaining <= 0
                        ? 1f
                        : 1f - _fadeInSamplesRemaining / (float)fadeLength;
                    output[index] = _samples.Dequeue() * Math.Clamp(fade, 0f, 1f);
                    if (_fadeInSamplesRemaining > 0) _fadeInSamplesRemaining--;
                }
            }

            return output;
        }

        public void ResetTransmissionBuffer()
        {
            lock (_gate)
            {
                _samples.Clear();
                _fadeInSamplesRemaining = AudioEngine.SampleRate * FadeInMilliseconds / 1000;
            }
        }

        public void ResetAll()
        {
            lock (_gate)
            {
                _samples.Clear();
                _resampler = CreateResampler();
                _fadeInSamplesRemaining = 0;
            }
        }

        internal int BufferedSamples
        {
            get { lock (_gate) return _samples.Count; }
        }

        private static WdlResampler CreateResampler()
        {
            var resampler = new WdlResampler();
            resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
            resampler.SetFilterParms();
            resampler.SetFeedMode(wantInputDriven: true);
            resampler.SetRates(CaptureSampleRate, AudioEngine.SampleRate);
            return resampler;
        }
    }
}
