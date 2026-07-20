using RadioRelay.Client.AudioEngineNs;
using RadioRelay.Client.Networking;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Tests;

public class LocalRadioPassthroughProcessorTests
{
    [Fact]
    public void Push_converter_produces_exactly_one_native_48khz_frame_without_padding()
    {
        var converter = new LocalPassthroughOutputConverter();
        var stereoFrame = new short[OpusCodec.FrameSize * 2];
        for (int i = 0; i < stereoFrame.Length; i += 2)
        {
            stereoFrame[i] = 1_000;
            stereoFrame[i + 1] = -1_000;
        }

        var output = converter.Convert(stereoFrame);

        Assert.Equal(OpusCodec.FrameSize * 3 * 8, output.Length);
        Assert.InRange(LocalPassthroughOutputConverter.AlgorithmicLatencyMilliseconds, 0, 0.5);
        Assert.Equal(20, AudioEngine.PassthroughOutputLatencyMilliseconds);
    }

    [Fact]
    public void No_active_radio_produces_no_passthrough_audio()
    {
        var processor = new LocalRadioPassthroughProcessor();

        var output = processor.Process(Array.Empty<(RadioChannel, byte[])>());

        Assert.Empty(output);
    }

    [Fact]
    public void Active_radio_produces_one_received_radio_frame()
    {
        var processor = new LocalRadioPassthroughProcessor();
        var radio = new RadioChannel { Frequency = 251.000f };
        var opus = new OpusCodec(AudioEngine.SampleRate).Encode(ToneFrame());

        var output = processor.Process(new[] { (radio, opus) });

        Assert.Equal(OpusCodec.FrameSize * 2, output.Length);
        Assert.Contains(output, sample => sample != 0);
    }

    [Fact]
    public void Simultaneous_radios_are_mixed_into_one_realtime_frame()
    {
        var processor = new LocalRadioPassthroughProcessor();
        var radios = new[]
        {
            new RadioChannel { Frequency = 25.000f },
            new RadioChannel { Frequency = 400.000f }
        };

        var encoder = new OpusCodec(AudioEngine.SampleRate);
        var output = processor.Process(new[]
        {
            (radios[0], encoder.Encode(ToneFrame())),
            (radios[1], encoder.Encode(ToneFrame()))
        });

        Assert.Equal(OpusCodec.FrameSize * 2, output.Length);
        Assert.Contains(output, sample => sample != 0);
    }

    [Fact]
    public void Simultaneous_radios_keep_full_mixer_gain_instead_of_being_averaged()
    {
        var first = new RadioChannel { IsIntercom = true, Ear = RadioEar.Both };
        var second = new RadioChannel { IsIntercom = true, Ear = RadioEar.Both };
        var opus = new OpusCodec(AudioEngine.SampleRate).Encode(ToneFrame());

        var single = new LocalRadioPassthroughProcessor().Process(new[] { (first, opus) });
        var combined = new LocalRadioPassthroughProcessor().Process(new[]
        {
            (first, opus),
            (second, opus)
        });

        int singlePeak = single.Max(sample => Math.Abs((int)sample));
        int combinedPeak = combined.Max(sample => Math.Abs((int)sample));
        Assert.True(combinedPeak > singlePeak * 1.70f,
            $"single={singlePeak}, combined={combinedPeak}");
        Assert.All(combined, sample => Assert.InRange(sample, short.MinValue, short.MaxValue));
    }

    [Fact]
    public void Input_gain_zero_silences_clean_intercom_passthrough()
    {
        var processor = new LocalRadioPassthroughProcessor();
        var intercom = new RadioChannel { IsIntercom = true };

        var silence = new short[OpusCodec.FrameSize];
        var opus = new OpusCodec(AudioEngine.SampleRate).Encode(silence);
        var output = processor.Process(new[] { (intercom, opus) });

        Assert.All(output, sample => Assert.InRange(sample, (short)-1, (short)1));
    }

    [Theory]
    [InlineData(RadioEar.Left, true, false)]
    [InlineData(RadioEar.Right, false, true)]
    [InlineData(RadioEar.Both, true, true)]
    public void Passthrough_honors_radio_ear_routing(
        RadioEar ear,
        bool expectLeft,
        bool expectRight)
    {
        var processor = new LocalRadioPassthroughProcessor();
        var radio = new RadioChannel { IsIntercom = true, Ear = ear };

        var opus = new OpusCodec(AudioEngine.SampleRate).Encode(ToneFrame());
        var output = processor.Process(new[] { (radio, opus) });
        var left = output.Where((_, index) => index % 2 == 0).ToArray();
        var right = output.Where((_, index) => index % 2 == 1).ToArray();

        Assert.Equal(expectLeft, left.Any(sample => sample != 0));
        Assert.Equal(expectRight, right.Any(sample => sample != 0));
        if (!expectLeft) Assert.All(left, sample => Assert.Equal(0, sample));
        if (!expectRight) Assert.All(right, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Passthrough_uses_the_exact_opus_packet_created_for_network_transmit()
    {
        var codec = new SecureAudioCodec(AudioEngine.SampleRate);
        var encoded = codec.EncodeAndEncrypt(ToneFrame(), NetOption.FromPasscode("recording-net"));

        Assert.NotEmpty(encoded.OpusPayload);
        Assert.NotEqual(encoded.Payload, encoded.OpusPayload);
        var decoded = new OpusCodec(AudioEngine.SampleRate).Decode(encoded.OpusPayload);
        Assert.Contains(decoded, sample => sample != 0);
    }

    [Fact]
    public void Passthrough_and_remote_rx_share_the_same_post_decode_processing()
    {
        var radio = new RadioChannel { Frequency = 251f, Ear = RadioEar.Left };
        var opus = new OpusCodec(AudioEngine.SampleRate).Encode(ToneFrame());
        var expectedDecoded = new OpusCodec(AudioEngine.SampleRate).Decode(opus);
        var expectedProfile = RadioEffectProfile.ForBand(
            RadioBandExtensions.FromFrequencyMHz(radio.Frequency),
            radio.IsIntercom,
            AudioEngine.SampleRate);
        expectedProfile.ResetReceive();
        var expectedNoise = new RadioNoiseGenerator();
        expectedNoise.Reset(RadioTransmissionNoiseSeed.FromOpusPayload(opus));
        var expected = RadioReceiveFrameProcessor.Process(
            expectedDecoded,
            radio,
            expectedProfile,
            expectedNoise);

        var actual = new LocalRadioPassthroughProcessor().Process(new[] { (radio, opus) });

        for (int i = 0; i < expected.Length; i++)
        {
            short expectedLeft = (short)Math.Clamp(expected[i] * 32767f, short.MinValue, short.MaxValue);
            Assert.Equal(expectedLeft, actual[i * 2]);
            Assert.Equal(0, actual[i * 2 + 1]);
        }
    }

    [Fact]
    public void Passthrough_state_resets_cleanly_for_each_ptt_session()
    {
        var radio = new RadioChannel { Frequency = 251f, Ear = RadioEar.Both };
        var opus = new OpusCodec(AudioEngine.SampleRate).Encode(ToneFrame());
        var processor = new LocalRadioPassthroughProcessor();

        var first = processor.Process(new[] { (radio, opus) });
        processor.Process(new[] { (radio, opus) });
        processor.ResetChannel(radio);
        var reset = processor.Process(new[] { (radio, opus) });

        Assert.Equal(first, reset);
    }

    [Fact]
    public void Shared_transmission_seed_keeps_passthrough_and_remote_noise_identical()
    {
        const uint seed = 0x5A17C0DEu;
        var radio = new RadioChannel { Frequency = 42.5f, Ear = RadioEar.Right };
        var opus = new OpusCodec(AudioEngine.SampleRate).Encode(ToneFrame());
        var expectedDecoded = new OpusCodec(AudioEngine.SampleRate).Decode(opus);
        var expectedProfile = RadioEffectProfile.ForBand(
            RadioBandExtensions.FromFrequencyMHz(radio.Frequency),
            radio.IsIntercom,
            AudioEngine.SampleRate);
        expectedProfile.ResetReceive();
        var expectedNoise = new RadioNoiseGenerator(seed);
        var expected = RadioReceiveFrameProcessor.Process(
            expectedDecoded,
            radio,
            expectedProfile,
            expectedNoise);

        var actual = new LocalRadioPassthroughProcessor().Process(new[] { (radio, opus, seed) });

        for (int i = 0; i < expected.Length; i++)
        {
            short expectedRight = (short)Math.Clamp(expected[i] * 32767f, short.MinValue, short.MaxValue);
            Assert.Equal(0, actual[i * 2]);
            Assert.Equal(expectedRight, actual[i * 2 + 1]);
        }
    }

    [Fact]
    public void Different_ptt_seeds_create_different_receiver_texture()
    {
        var radio = new RadioChannel { Frequency = 251f, Ear = RadioEar.Both };
        var opus = new OpusCodec(AudioEngine.SampleRate).Encode(ToneFrame());
        var processor = new LocalRadioPassthroughProcessor();

        var first = processor.Process(new[] { (radio, opus, 101u) });
        processor.ResetChannel(radio);
        var second = processor.Process(new[] { (radio, opus, 202u) });

        Assert.False(first.SequenceEqual(second));
    }

    [Fact]
    public void Continuous_tone_does_not_acquire_periodic_digital_silence()
    {
        var processor = new LocalRadioPassthroughProcessor();
        var radio = new RadioChannel { Frequency = 251.000f };
        var encoder = new OpusCodec(AudioEngine.SampleRate);
        int samplePosition = 0;
        int longestZeroRun = 0;
        int zeroRun = 0;

        for (int frameIndex = 0; frameIndex < 150; frameIndex++)
        {
            var frame = new short[OpusCodec.FrameSize];
            for (int i = 0; i < frame.Length; i++, samplePosition++)
                frame[i] = (short)(Math.Sin(samplePosition * 2 * Math.PI * 440 / AudioEngine.SampleRate) * 8_000);

            foreach (short sample in processor.Process(new[] { (radio, encoder.Encode(frame)) }))
            {
                if (sample == 0)
                {
                    zeroRun++;
                    longestZeroRun = Math.Max(longestZeroRun, zeroRun);
                }
                else
                {
                    zeroRun = 0;
                }
            }
        }

        Assert.InRange(longestZeroRun, 0, 8);
    }

    [Fact]
    public void Complete_passthrough_chain_preserves_duration_without_digital_silence_gaps()
    {
        var encoder = new OpusCodec(AudioEngine.SampleRate);
        var processor = new LocalRadioPassthroughProcessor();
        var converter = new LocalPassthroughOutputConverter();
        var radio = new RadioChannel { IsIntercom = true, Ear = RadioEar.Both };
        int samplePosition = 0;
        int outputFrames = 0;
        int currentSilentFrames = 0;
        int longestSilentFrames = 0;

        for (int frameIndex = 0; frameIndex < 150; frameIndex++)
        {
            var frame = new short[OpusCodec.FrameSize];
            for (int i = 0; i < frame.Length; i++, samplePosition++)
                frame[i] = (short)(Math.Sin(samplePosition * 2 * Math.PI * 440 / AudioEngine.SampleRate) * 8_000);

            var stereo16 = processor.Process(new[] { (radio, encoder.Encode(frame)) });
            var stereo48 = converter.Convert(stereo16);
            outputFrames += stereo48.Length / 8;

            for (int offset = 0; offset < stereo48.Length; offset += 8)
            {
                float left = BitConverter.ToSingle(stereo48, offset);
                float right = BitConverter.ToSingle(stereo48, offset + 4);
                if (left == 0f && right == 0f)
                {
                    currentSilentFrames++;
                    longestSilentFrames = Math.Max(longestSilentFrames, currentSilentFrames);
                }
                else
                {
                    currentSilentFrames = 0;
                }
            }
        }

        Assert.Equal(150 * AudioEngine.PassthroughOutputSampleRate / 50, outputFrames);
        Assert.InRange(longestSilentFrames, 0, AudioEngine.PassthroughOutputSampleRate / 1000);
    }

    private static short[] ToneFrame()
    {
        var frame = new short[OpusCodec.FrameSize];
        for (int i = 0; i < frame.Length; i++)
            frame[i] = (short)(Math.Sin(i * 0.12) * 8_000);
        return frame;
    }
}
