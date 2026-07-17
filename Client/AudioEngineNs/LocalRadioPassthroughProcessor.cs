using System;
using System.Collections.Generic;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Client.AudioEngineNs
{
    /// Decodes the exact Opus packets emitted by the real transmit path and
    /// sends them through the same post-decode processing used by remote RX.
    /// Network jitter and RX key sounds are deliberately not part of this
    /// local recording path.
    internal sealed class LocalRadioPassthroughProcessor
    {
        private sealed class ChannelState
        {
            public required RadioBand Band { get; init; }
            public required bool IsIntercom { get; init; }
            public required RadioEffectProfile Profile { get; init; }
            public OpusCodec Decoder { get; } = new(AudioEngine.SampleRate);
            public int NoiseLoopPosition;
        }

        private readonly Dictionary<RadioChannel, ChannelState> _states = new();

        public short[] Process(
            IReadOnlyList<(RadioChannel Channel, byte[] OpusPayload)> encodedFrames)
        {
            if (encodedFrames.Count == 0) return Array.Empty<short>();

            var mixedLeft = new float[OpusCodec.FrameSize];
            var mixedRight = new float[OpusCodec.FrameSize];
            int leftContributors = 0;
            int rightContributors = 0;
            foreach (var encodedFrame in encodedFrames)
            {
                if (encodedFrame.Channel.Ear != RadioEar.Right) leftContributors++;
                if (encodedFrame.Channel.Ear != RadioEar.Left) rightContributors++;
            }

            foreach (var encodedFrame in encodedFrames)
            {
                var channel = encodedFrame.Channel;
                var state = GetState(channel);
                var decodedPcm = state.Decoder.Decode(encodedFrame.OpusPayload);
                var receivedFrame = RadioReceiveFrameProcessor.Process(
                    decodedPcm,
                    channel,
                    state.Profile,
                    ref state.NoiseLoopPosition);

                var (leftGain, rightGain) = EarGains(channel.Ear);
                float leftScale = leftContributors == 0 ? 0f : leftGain / leftContributors;
                float rightScale = rightContributors == 0 ? 0f : rightGain / rightContributors;
                for (int i = 0; i < receivedFrame.Length; i++)
                {
                    mixedLeft[i] = Math.Clamp(mixedLeft[i] + receivedFrame[i] * leftScale, -1f, 1f);
                    mixedRight[i] = Math.Clamp(mixedRight[i] + receivedFrame[i] * rightScale, -1f, 1f);
                }
            }

            return ToStereoPcm(mixedLeft, mixedRight);
        }

        public void Reset()
        {
            _states.Clear();
        }

        private ChannelState GetState(RadioChannel channel)
        {
            var band = RadioBandExtensions.FromFrequencyMHz(channel.Frequency);
            if (_states.TryGetValue(channel, out var state) &&
                state.Band == band &&
                state.IsIntercom == channel.IsIntercom)
            {
                return state;
            }

            state = new ChannelState
            {
                Band = band,
                IsIntercom = channel.IsIntercom,
                Profile = RadioEffectProfile.ForBand(band, channel.IsIntercom, AudioEngine.SampleRate)
            };
            _states[channel] = state;
            return state;
        }

        private static (float left, float right) EarGains(RadioEar ear) => ear switch
        {
            RadioEar.Left => (1f, 0f),
            RadioEar.Right => (0f, 1f),
            _ => (0.70710677f, 0.70710677f)
        };

        private static short[] ToStereoPcm(float[] left, float[] right)
        {
            var pcm = new short[left.Length * 2];
            for (int i = 0; i < left.Length; i++)
            {
                pcm[i * 2] = (short)Math.Clamp(left[i] * 32767f, short.MinValue, short.MaxValue);
                pcm[i * 2 + 1] = (short)Math.Clamp(right[i] * 32767f, short.MinValue, short.MaxValue);
            }
            return pcm;
        }
    }
}
