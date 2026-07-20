using RadioRelay.Client.AudioEngineNs;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Tests;

public class JitterBufferEpochTests
{
    [Fact]
    public void Duplicate_start_in_same_epoch_does_not_reset_decoder_or_playout()
    {
        var sender = new OpusCodec(AudioEngine.SampleRate);
        var cleanDecoder = new OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new OpusCodec(AudioEngine.SampleRate));
        var firstFrame = sender.Encode(Tone(410f));
        var secondFrame = sender.Encode(Tone(730f));

        Assert.True(jitter.OnFrameReceived(41, 1, firstFrame, isStart: true, isEnd: false,
            JitterAcquisitionMode.Immediate));
        Assert.Equal(cleanDecoder.Decode(firstFrame), jitter.Tick().Pcm);

        Assert.False(jitter.OnFrameReceived(41, 1, firstFrame, isStart: true, isEnd: false,
            JitterAcquisitionMode.Immediate));
        Assert.True(jitter.OnFrameReceived(41, 2, secondFrame, isStart: false, isEnd: false));

        Assert.Equal(cleanDecoder.Decode(secondFrame), jitter.Tick().Pcm);
    }

    [Fact]
    public void Reordered_redundant_start_hint_restores_the_earliest_frame_before_playout()
    {
        var sender = new OpusCodec(AudioEngine.SampleRate);
        var cleanDecoder = new OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new OpusCodec(AudioEngine.SampleRate));
        var first = sender.Encode(Tone(410f));
        var second = sender.Encode(Tone(610f));
        var third = sender.Encode(Tone(810f));

        Assert.True(jitter.OnFrameReceived(51, 2, second, isStart: true, isEnd: false));
        Assert.True(jitter.OnFrameReceived(51, 3, third, isStart: true, isEnd: false));
        Assert.True(jitter.OnFrameReceived(51, 1, first, isStart: true, isEnd: false));

        var played = jitter.Tick();
        Assert.True(played.IsFirstFrame);
        Assert.Equal(cleanDecoder.Decode(first), played.Pcm);
    }

    [Fact]
    public void Reordered_end_waits_for_every_sequence_through_its_terminal_sequence()
    {
        var sender = new OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new OpusCodec(AudioEngine.SampleRate));
        var first = sender.Encode(Tone(300f));
        var second = sender.Encode(Tone(500f));
        var third = sender.Encode(Tone(700f));

        Assert.True(jitter.OnFrameReceived(80, 1, first, isStart: true, isEnd: false));
        Assert.True(jitter.OnFrameReceived(80, 3, third, isStart: false, isEnd: false));
        Assert.True(jitter.OnFrameReceived(80, 3, Array.Empty<byte>(), isStart: false, isEnd: true));

        Assert.Null(jitter.Tick().Pcm); // Hold the early End for reordering.
        Assert.True(jitter.OnFrameReceived(80, 2, second, isStart: false, isEnd: false));

        var playedFirst = jitter.Tick();
        var playedSecond = jitter.Tick();
        var playedThird = jitter.Tick();
        Assert.True(playedFirst.IsFirstFrame);
        Assert.False(playedFirst.IsLastFrame);
        Assert.False(playedSecond.IsLastFrame);
        Assert.True(playedThird.IsLastFrame);
        Assert.NotNull(playedThird.Pcm);
    }

    [Fact]
    public void Missing_frame_before_terminal_gets_one_plc_tick_instead_of_truncating_tail()
    {
        var sender = new OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new OpusCodec(AudioEngine.SampleRate));
        var first = sender.Encode(Tone(360f));
        _ = sender.Encode(Tone(520f)); // Advance the encoder for the lost frame.
        var third = sender.Encode(Tone(680f));

        Assert.True(jitter.OnFrameReceived(81, 1, first, isStart: true, isEnd: false,
            JitterAcquisitionMode.Immediate));
        Assert.True(jitter.OnFrameReceived(81, 3, third, isStart: false, isEnd: false));
        Assert.True(jitter.OnFrameReceived(81, 3, Array.Empty<byte>(), isStart: false, isEnd: true));

        Assert.NotNull(jitter.Tick().Pcm);
        var concealed = jitter.Tick();
        var final = jitter.Tick();

        Assert.NotNull(concealed.Pcm);
        Assert.False(concealed.IsLastFrame);
        Assert.NotNull(final.Pcm);
        Assert.True(final.IsLastFrame);
    }

    [Fact]
    public void Explicit_buffered_acquisition_recovers_when_start_datagram_was_lost()
    {
        var sender = new OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new OpusCodec(AudioEngine.SampleRate));
        var frame10 = sender.Encode(Tone(400f));
        var frame11 = sender.Encode(Tone(500f));
        var frame12 = sender.Encode(Tone(600f));

        Assert.False(jitter.OnFrameReceived(500, 10, frame10, isStart: false, isEnd: false));
        Assert.True(jitter.OnFrameReceived(500, 10, frame10, isStart: false, isEnd: false,
            JitterAcquisitionMode.Buffered));
        Assert.True(jitter.OnFrameReceived(500, 11, frame11, isStart: false, isEnd: false));
        Assert.True(jitter.OnFrameReceived(500, 12, frame12, isStart: false, isEnd: false));

        var result = jitter.Tick();
        Assert.True(result.IsFirstFrame);
        Assert.NotNull(result.Pcm);
    }

    [Fact]
    public void Delayed_packets_from_retired_epoch_cannot_replace_current_stream()
    {
        var firstSender = new OpusCodec(AudioEngine.SampleRate);
        var secondSender = new OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new OpusCodec(AudioEngine.SampleRate));
        var oldFrame = firstSender.Encode(Tone(440f));
        var newFrame = secondSender.Encode(Tone(880f));

        Assert.True(jitter.OnFrameReceived(100, 1, oldFrame, isStart: true, isEnd: false,
            JitterAcquisitionMode.Immediate));
        Assert.NotNull(jitter.Tick().Pcm);

        Assert.True(jitter.OnFrameReceived(101, 1, newFrame, isStart: true, isEnd: false,
            JitterAcquisitionMode.Immediate));
        Assert.False(jitter.OnFrameReceived(100, 1, oldFrame, isStart: true, isEnd: false,
            JitterAcquisitionMode.Immediate));

        Assert.Equal((ulong)101, jitter.ActiveTransmissionId);
        Assert.True(jitter.Tick().IsFirstFrame);
    }

    [Fact]
    public void Reorder_storage_is_bounded_and_rejects_unreasonable_future_jumps()
    {
        var jitter = new JitterBuffer(new OpusCodec(AudioEngine.SampleRate));
        Assert.True(jitter.OnFrameReceived(200, 0, Array.Empty<byte>(), isStart: true, isEnd: false));

        for (int i = 0; i < 400; i++)
            jitter.OnFrameReceived(200, unchecked((ushort)i), new byte[] { 1 }, isStart: false, isEnd: false);

        Assert.InRange(jitter.BufferedFrameCount, 1, 128);
        Assert.False(jitter.OnFrameReceived(200, 1000, new byte[] { 1 }, isStart: false, isEnd: false));
    }

    [Fact]
    public void Terminal_sequence_and_playout_handle_ushort_wraparound()
    {
        var sender = new OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new OpusCodec(AudioEngine.SampleRate));
        var beforeWrap = sender.Encode(Tone(330f));
        var afterWrap = sender.Encode(Tone(660f));

        Assert.True(jitter.OnFrameReceived(300, ushort.MaxValue, beforeWrap, isStart: true, isEnd: false,
            JitterAcquisitionMode.Immediate));
        Assert.True(jitter.OnFrameReceived(300, 0, afterWrap, isStart: false, isEnd: false));
        Assert.True(jitter.OnFrameReceived(300, 0, Array.Empty<byte>(), isStart: false, isEnd: true));

        Assert.False(jitter.Tick().IsLastFrame);
        Assert.True(jitter.Tick().IsLastFrame);
    }

    private static short[] Tone(float frequency)
    {
        var pcm = new short[AudioEngine.SampleRate / 50];
        for (int i = 0; i < pcm.Length; i++)
        {
            float sample = MathF.Sin(MathF.Tau * frequency * i / AudioEngine.SampleRate) * 0.35f;
            pcm[i] = (short)(sample * short.MaxValue);
        }

        return pcm;
    }
}
