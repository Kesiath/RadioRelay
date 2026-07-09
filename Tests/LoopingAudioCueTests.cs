using RadioRelay.Client.AudioEngineNs;

namespace RadioRelay.Tests;

public class LoopingAudioCueTests
{
    [Fact]
    public void ReadFrame_continues_looping_after_source_duration()
    {
        var cue = new LoopingAudioCue(new[] { 0.1f, -0.2f, 0.3f });

        var first = cue.ReadFrame(5, 1f);
        var second = cue.ReadFrame(5, 1f);

        Assert.Equal(new[] { 0.1f, -0.2f, 0.3f, 0.1f, -0.2f }, first);
        Assert.Equal(new[] { 0.3f, 0.1f, -0.2f, 0.3f, 0.1f }, second);
    }

    [Fact]
    public void Reset_restarts_loop_from_beginning()
    {
        var cue = new LoopingAudioCue(new[] { 0.25f, 0.5f });
        cue.ReadFrame(3, 1f);

        cue.Reset();
        var frame = cue.ReadFrame(3, 0.5f);

        Assert.Equal(new[] { 0.125f, 0.25f, 0.125f }, frame);
    }
}
