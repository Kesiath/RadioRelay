using NAudio.Wave;
using RadioRelay.Client.AudioEngineNs;

namespace RadioRelay.Tests;

public class RadioReceiveMuteTests
{
    [Fact]
    public void ShouldMuteReceivedAudio_returns_true_while_local_radio_is_transmitting()
    {
        Assert.True(RadioReceiveMute.ShouldMuteReceivedAudio(localTransmitting: true));
        Assert.False(RadioReceiveMute.ShouldMuteReceivedAudio(localTransmitting: false));
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(0.01f, false)]
    [InlineData(1f, false)]
    public void Zero_volume_disables_the_receiver(float volume, bool expectedDisabled)
    {
        Assert.Equal(expectedDisabled, RadioReceiveMute.IsReceiveDisabled(volume));
        Assert.Equal(!expectedDisabled, RadioReceiveMute.CanStartTransmission(volume));
    }

    [Fact]
    public void Reset_discards_pending_jitter_playout_when_local_transmit_starts()
    {
        var jitter = new JitterBuffer(new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate));
        jitter.OnFrameReceived(sequence: 1, opusBytes: new byte[] { 1, 2, 3 }, isStart: true, isEnd: false);

        jitter.Reset();

        Assert.False(jitter.Tick().IsFirstFrame);
    }

    [Fact]
    public void Mid_stream_packet_after_local_transmit_release_reopens_jitter_playout_without_rebuffering_delay()
    {
        var jitter = new JitterBuffer(new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate));

        jitter.OnFrameReceived(sequence: 10, opusBytes: new byte[] { 1, 2, 3 }, isStart: false, isEnd: false, forceStart: true);

        Assert.True(jitter.Tick().IsFirstFrame);
    }

    [Fact]
    public void Empty_end_packet_marks_jitter_done_without_waiting_for_packet_loss_timeout()
    {
        var jitter = new JitterBuffer(new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate));

        jitter.OnFrameReceived(sequence: 10, opusBytes: Array.Empty<byte>(), isStart: false, isEnd: true, forceStart: true);

        var result = jitter.Tick();
        Assert.True(result.IsFirstFrame);
        Assert.True(result.IsLastFrame);
    }

    [Fact]
    public void Force_start_after_talkover_decodes_remaining_sender_like_a_fresh_receiver()
    {
        var previousSenderEncoder = new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate);
        var remainingSenderEncoder = new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate));
        var cleanReceiver = new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate);

        var previousFrame = previousSenderEncoder.Encode(Tone(330f));
        var remainingFrame = remainingSenderEncoder.Encode(Tone(880f));

        jitter.OnFrameReceived(sequence: 1, previousFrame, isStart: true, isEnd: false, forceStart: true);
        Assert.NotNull(jitter.Tick().Pcm);

        jitter.OnFrameReceived(sequence: 20, remainingFrame, isStart: false, isEnd: false, forceStart: true);
        var reacquired = jitter.Tick().Pcm;
        var clean = cleanReceiver.Decode(remainingFrame);

        Assert.Equal(clean, reacquired);
    }

    [Fact]
    public void Malformed_accepted_opus_frame_is_dropped_without_starting_warbling_playout()
    {
        var jitter = new JitterBuffer(new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate));
        var malformedStructurallyValidPayload = new byte[] { 0x7f, 0xff, 0x00, 0x00, 0x13, 0x37 };

        jitter.OnFrameReceived(sequence: 10, malformedStructurallyValidPayload, isStart: true, isEnd: false, forceStart: true);

        var firstTick = jitter.Tick();
        var secondTick = jitter.Tick();

        Assert.False(firstTick.IsFirstFrame);
        Assert.Null(firstTick.Pcm);
        Assert.False(secondTick.IsFirstFrame);
        Assert.Null(secondTick.Pcm);
    }

    [Fact]
    public void Fresh_start_after_malformed_opus_frame_decodes_with_clean_receiver_state()
    {
        var sender = new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate);
        var cleanReceiver = new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new RadioRelay.Shared.Audio.OpusCodec(AudioEngine.SampleRate));
        var malformedStructurallyValidPayload = new byte[] { 0x7f, 0xff, 0x00, 0x00, 0x13, 0x37 };
        var validFrame = sender.Encode(Tone(660f));

        jitter.OnFrameReceived(sequence: 10, malformedStructurallyValidPayload, isStart: true, isEnd: false, forceStart: true);
        Assert.Null(jitter.Tick().Pcm);

        jitter.OnFrameReceived(sequence: 20, validFrame, isStart: true, isEnd: false, forceStart: true);
        var recovered = jitter.Tick().Pcm;
        var clean = cleanReceiver.Decode(validFrame);

        Assert.Equal(clean, recovered);
    }

    [Fact]
    public void Talkover_warning_volume_is_independent_from_tx_click_volume()
    {
        var cue = new LoopingAudioCue(new[] { 0.5f, -0.5f });

        var mutedTxClickWarning = cue.ReadFrame(2, gain: 0.25f);

        Assert.Equal(new[] { 0.125f, -0.125f }, mutedTxClickWarning);
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
