using System;
using System.Collections.Generic;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Client.AudioEngineNs
{
    internal readonly record struct LocalRadioPassthroughFrame(
        RadioChannel Channel,
        byte[] OpusPayload,
        uint TransmissionAudioSeed,
        ulong TransmissionId,
        float Frequency,
        bool IsIntercom,
        RadioEar Ear);

    /// <summary>
    /// Runs transmitted Opus frames through receive-equivalent DSP for local recording.
    /// </summary>
    internal sealed class LocalRadioPassthroughProcessor
    {
        internal const int BoundaryRampMilliseconds = 3;

        private sealed class ChannelState
        {
            public required RadioBand Band { get; init; }
            public required bool IsIntercom { get; init; }
            public required ulong TransmissionId { get; init; }
            public required RadioEffectProfile Profile { get; init; }
            public OpusCodec Decoder { get; } = new(AudioEngine.SampleRate);
            public RadioNoiseGenerator Noise { get; } = new();
        }

        private readonly Dictionary<RadioChannel, ChannelState> _states = new();
        private bool _boundaryActive;
        private bool _fadeInNextFrame;
        private bool _hasBoundaryOutput;
        private short _lastLeft;
        private short _lastRight;

        public void BeginTransmission()
        {
            _boundaryActive = true;
            _fadeInNextFrame = true;
            _hasBoundaryOutput = false;
            _lastLeft = 0;
            _lastRight = 0;
        }

        public short[] EndTransmission()
        {
            var tail = _boundaryActive && _hasBoundaryOutput
                ? CreateEndRamp(_lastLeft, _lastRight)
                : Array.Empty<short>();
            ResetBoundary();
            return tail;
        }

        public short[] Process(
            IReadOnlyList<(RadioChannel Channel, byte[] OpusPayload)> encodedFrames)
        {
            var seededFrames = new (RadioChannel Channel, byte[] OpusPayload, uint TransmissionAudioSeed)[encodedFrames.Count];
            for (int i = 0; i < encodedFrames.Count; i++)
            {
                var frame = encodedFrames[i];
                seededFrames[i] = (
                    frame.Channel,
                    frame.OpusPayload,
                    RadioTransmissionNoiseSeed.FromOpusPayload(frame.OpusPayload));
            }

            return Process(seededFrames);
        }

        public short[] Process(
            IReadOnlyList<(RadioChannel Channel, byte[] OpusPayload, uint TransmissionAudioSeed)> encodedFrames)
        {
            var frames = new LocalRadioPassthroughFrame[encodedFrames.Count];
            for (int i = 0; i < encodedFrames.Count; i++)
            {
                var frame = encodedFrames[i];
                frames[i] = new LocalRadioPassthroughFrame(
                    frame.Channel,
                    frame.OpusPayload,
                    frame.TransmissionAudioSeed,
                    0,
                    frame.Channel.Frequency,
                    frame.Channel.IsIntercom,
                    frame.Channel.Ear);
            }

            return Process(frames);
        }

        public short[] Process(IReadOnlyList<LocalRadioPassthroughFrame> encodedFrames)
        {
            if (encodedFrames.Count == 0) return Array.Empty<short>();

            var mixedLeft = new float[OpusCodec.FrameSize];
            var mixedRight = new float[OpusCodec.FrameSize];

            foreach (var encodedFrame in encodedFrames)
            {
                var channel = encodedFrame.Channel;
                var state = GetState(
                    channel,
                    encodedFrame.TransmissionAudioSeed,
                    encodedFrame.TransmissionId,
                    encodedFrame.Frequency,
                    encodedFrame.IsIntercom,
                    encodedFrame.OpusPayload);
                var decodedPcm = state.Decoder.Decode(encodedFrame.OpusPayload);
                var receivedFrame = RadioReceiveFrameProcessor.Process(
                    decodedPcm,
                    encodedFrame.Frequency,
                    encodedFrame.IsIntercom,
                    state.Profile,
                    state.Noise);

                var (leftGain, rightGain) = EarGains(encodedFrame.Ear);
                for (int i = 0; i < receivedFrame.Length; i++)
                {
                    mixedLeft[i] += receivedFrame[i] * leftGain;
                    mixedRight[i] += receivedFrame[i] * rightGain;
                }
            }

            var pcm = ToStereoPcm(mixedLeft, mixedRight);
            if (_boundaryActive)
            {
                if (_fadeInNextFrame)
                {
                    ApplyStartRamp(pcm);
                    _fadeInNextFrame = false;
                }

                if (pcm.Length >= 2)
                {
                    _lastLeft = pcm[^2];
                    _lastRight = pcm[^1];
                    _hasBoundaryOutput = true;
                }
            }

            return pcm;
        }

        public void Reset()
        {
            _states.Clear();
            ResetBoundary();
        }

        public void ResetChannel(RadioChannel channel) => _states.Remove(channel);

        private ChannelState GetState(
            RadioChannel channel,
            uint transmittedSeed,
            ulong transmissionId,
            float frequency,
            bool isIntercom,
            ReadOnlySpan<byte> opusPayload)
        {
            var band = RadioBandExtensions.FromFrequencyMHz(frequency);
            if (_states.TryGetValue(channel, out var state) &&
                state.Band == band &&
                state.IsIntercom == isIntercom &&
                (transmissionId == 0 || state.TransmissionId == transmissionId))
            {
                return state;
            }

            state = new ChannelState
            {
                Band = band,
                IsIntercom = isIntercom,
                TransmissionId = transmissionId,
                Profile = RadioEffectProfile.ForBand(
                    band,
                    isIntercom,
                    AudioEngine.SampleRate)
            };
            state.Profile.ResetReceive();
            state.Noise.Reset(RadioTransmissionNoiseSeed.Resolve(transmittedSeed, opusPayload));
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
                pcm[i * 2] = (short)Math.Clamp(
                    SoftLimiterSampleProvider.Limit(left[i]) * 32767f,
                    short.MinValue,
                    short.MaxValue);
                pcm[i * 2 + 1] = (short)Math.Clamp(
                    SoftLimiterSampleProvider.Limit(right[i]) * 32767f,
                    short.MinValue,
                    short.MaxValue);
            }
            return pcm;
        }

        internal static void ApplyStartRamp(short[] stereoPcm)
        {
            int rampFrames = Math.Min(
                stereoPcm.Length / 2,
                AudioEngine.SampleRate * BoundaryRampMilliseconds / 1000);
            if (rampFrames <= 1) return;

            for (int frame = 0; frame < rampFrames; frame++)
            {
                float gain = frame / (float)(rampFrames - 1);
                int offset = frame * 2;
                stereoPcm[offset] = (short)Math.Round(stereoPcm[offset] * gain);
                stereoPcm[offset + 1] = (short)Math.Round(stereoPcm[offset + 1] * gain);
            }
        }

        internal static short[] CreateEndRamp(short left, short right)
        {
            int rampFrames = Math.Max(
                2,
                AudioEngine.SampleRate * BoundaryRampMilliseconds / 1000);
            var tail = new short[rampFrames * 2];
            for (int frame = 0; frame < rampFrames; frame++)
            {
                float gain = 1f - (frame + 1f) / rampFrames;
                int offset = frame * 2;
                tail[offset] = (short)Math.Round(left * gain);
                tail[offset + 1] = (short)Math.Round(right * gain);
            }

            return tail;
        }

        private void ResetBoundary()
        {
            _boundaryActive = false;
            _fadeInNextFrame = false;
            _hasBoundaryOutput = false;
            _lastLeft = 0;
            _lastRight = 0;
        }
    }
}
