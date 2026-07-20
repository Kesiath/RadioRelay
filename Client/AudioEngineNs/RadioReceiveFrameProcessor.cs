using System;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Client.AudioEngineNs
{
    /// Shared post-Opus receive path used by both remote playback and the
    /// local recording passthrough. Keeping this in one implementation is
    /// what makes passthrough audio receive-equivalent rather than an
    /// approximation of it.
    internal static class RadioReceiveFrameProcessor
    {
        public static float[] Process(
            short[]? decodedPcm,
            RadioChannel channel,
            RadioEffectProfile profile,
            ref int noiseLoopPosition)
        {
            var frame = new float[OpusCodec.FrameSize];
            if (decodedPcm != null)
            {
                int count = Math.Min(frame.Length, decodedPcm.Length);
                for (int i = 0; i < count; i++)
                    frame[i] = decodedPcm[i] / 32768f;
                profile.RxEffect.Process(frame);
            }

            if (channel.IsIntercom) return frame;

            var noise = SoundLibrary.GetBandNoiseLoop(RadioBandExtensions.FromFrequencyMHz(channel.Frequency));
            if (noise.Length == 0) return frame;

            int position = noiseLoopPosition;
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = Math.Clamp(frame[i] + noise[position] * profile.NoiseGainLinear, -1f, 1f);
                position++;
                if (position >= noise.Length) position = 0;
            }
            noiseLoopPosition = position;
            return frame;
        }
    }
}
