using System;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Applies shared post-Opus processing for remote playback and local passthrough.
    /// </summary>
    internal static class RadioReceiveFrameProcessor
    {
        public static float[] Process(
            short[]? decodedPcm,
            RadioChannel channel,
            RadioEffectProfile profile,
            RadioNoiseGenerator noiseGenerator) =>
            Process(decodedPcm, channel.Frequency, channel.IsIntercom, profile, noiseGenerator);

        public static float[] Process(
            short[]? decodedPcm,
            float frequency,
            bool isIntercom,
            RadioEffectProfile profile,
            RadioNoiseGenerator noiseGenerator)
        {
            var frame = new float[OpusCodec.FrameSize];
            if (decodedPcm != null)
            {
                int count = Math.Min(frame.Length, decodedPcm.Length);
                for (int i = 0; i < count; i++)
                    frame[i] = decodedPcm[i] / 32768f;
            }

            return ApplyReceiveChain(frame, frequency, isIntercom, profile, noiseGenerator);
        }

        public static float[] ProcessSamples(
            float[] samples,
            RadioChannel channel,
            RadioEffectProfile profile,
            RadioNoiseGenerator noiseGenerator) =>
            ProcessSamples(samples, channel.Frequency, channel.IsIntercom, profile, noiseGenerator);

        public static float[] ProcessSamples(
            float[] samples,
            float frequency,
            bool isIntercom,
            RadioEffectProfile profile,
            RadioNoiseGenerator noiseGenerator)
        {
            var frame = new float[samples.Length];
            Array.Copy(samples, frame, samples.Length);
            return ApplyReceiveChain(frame, frequency, isIntercom, profile, noiseGenerator);
        }

        private static float[] ApplyReceiveChain(
            float[] frame,
            float frequency,
            bool isIntercom,
            RadioEffectProfile profile,
            RadioNoiseGenerator noiseGenerator)
        {

            // Process receiver noise through the same chain as voice.
            if (!isIntercom)
                noiseGenerator.AddTo(frame, profile, frequency);

            profile.RxEffect.Process(frame);
            return frame;
        }
    }
}
