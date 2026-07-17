using RadioRelay.Client.AudioEngineNs;

namespace RadioRelay.Tests;

public class MicTestMonitorTests
{
    [Fact]
    public void Mic_test_can_be_toggled_without_starting_audio_devices()
    {
        using var engine = new AudioEngine(new(), startAudioDevices: false);

        Assert.False(engine.IsMicTestActive);

        engine.SetMicTestActive(true);
        Assert.True(engine.IsMicTestActive);

        engine.SetMicTestActive(false);
        Assert.False(engine.IsMicTestActive);
    }

    [Fact]
    public void Mic_test_primes_enough_silence_for_output_callback_blocking()
    {
        using var engine = new AudioEngine(new(), startAudioDevices: false);

        engine.SetMicTestActive(true);

        var bufferField = typeof(AudioEngine).GetField(
            "_micTestBuffer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var buffer = Assert.IsType<NAudio.Wave.BufferedWaveProvider>(bufferField!.GetValue(engine));
        int expectedBytes = AudioEngine.SampleRate * 2 * AudioEngine.MicTestPrebufferMilliseconds / 1000;
        Assert.Equal(expectedBytes, buffer.BufferedBytes);

        engine.SetMicTestActive(false);
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public void Mic_test_pcm_applies_input_gain_and_clips_samples()
    {
        var captured = ToPcm(1_000, -1_000, 20_000, -20_000);

        var monitored = AudioEngine.CreateMicTestPcm(captured, captured.Length, inputGain: 2f);

        Assert.Equal(new short[] { 2_000, -2_000, short.MaxValue, short.MinValue }, FromPcm(monitored));
    }

    [Fact]
    public void Mic_test_pcm_honors_recorded_length_and_zero_gain()
    {
        var captured = ToPcm(1_000, -1_000, 2_000);

        var monitored = AudioEngine.CreateMicTestPcm(captured, bytesRecorded: 4, inputGain: 0f);

        Assert.Equal(new short[] { 0, 0 }, FromPcm(monitored));
    }

    private static byte[] ToPcm(params short[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return pcm;
    }

    private static short[] FromPcm(byte[] pcm)
    {
        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
        return samples;
    }
}
