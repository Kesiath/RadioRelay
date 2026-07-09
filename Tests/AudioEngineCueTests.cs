using NAudio.Wave;
using RadioRelay.Client.AudioEngineNs;

namespace RadioRelay.Tests;

public class AudioEngineCueTests
{
    [Fact]
    public void ReplaceBufferedSamples_discards_pending_system_cue_audio_before_adding_latest_cue()
    {
        var buffer = new BufferedWaveProvider(new WaveFormat(16000, 16, 1));
        var staleConnectCue = new byte[800];
        var disconnectCue = new byte[320];

        buffer.AddSamples(staleConnectCue, 0, staleConnectCue.Length);

        AudioEngine.ReplaceBufferedSamples(buffer, disconnectCue);

        Assert.Equal(disconnectCue.Length, buffer.BufferedBytes);

        var nextConnectCue = new byte[160];
        AudioEngine.ReplaceBufferedSamples(buffer, nextConnectCue);

        Assert.Equal(nextConnectCue.Length, buffer.BufferedBytes);
    }
}
