using RadioRelay.Client.AudioEngineNs;

namespace RadioRelay.Tests;

public class RadioCollisionDestructionModelTests
{
    [Fact]
    public void Process_inactive_leaves_samples_unchanged()
    {
        var model = new RadioCollisionDestructionModel(AudioEngine.SampleRate);
        var frame = Enumerable.Range(0, 320).Select(i => MathF.Sin(i * 0.05f) * 0.4f).ToArray();
        var original = frame.ToArray();

        model.Process(frame, active: false);

        Assert.Equal(original, frame);
    }

    [Fact]
    public void Process_active_destructively_changes_captured_signal_without_clipping()
    {
        var model = new RadioCollisionDestructionModel(AudioEngine.SampleRate);
        var frame = Enumerable.Range(0, 320).Select(i => MathF.Sin(i * 0.08f) * 0.55f).ToArray();
        var original = frame.ToArray();

        model.Process(frame, active: true);

        Assert.NotEqual(original, frame);
        Assert.All(frame, sample => Assert.InRange(sample, -1f, 1f));
        Assert.True(Rms(frame) < Rms(original), "collision should partially cancel/duck the captured signal");
        Assert.True(ZeroCrossings(frame) > ZeroCrossings(original), "collision should add scratchy high-frequency disruption");
    }

    [Fact]
    public void Process_active_evolves_over_time_for_flutter_and_whoosh()
    {
        var model = new RadioCollisionDestructionModel(AudioEngine.SampleRate);
        var first = Enumerable.Range(0, 320).Select(i => MathF.Sin(i * 0.06f) * 0.5f).ToArray();
        var second = first.ToArray();

        model.Process(first, active: true);
        model.Process(second, active: true);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Process_active_on_quiet_signal_adds_hiss_not_recorded_squelch()
    {
        var model = new RadioCollisionDestructionModel(AudioEngine.SampleRate);
        var frame = new float[320];

        model.Process(frame, active: true);

        var staticRms = Rms(frame);
        Assert.True(staticRms > 0.01f);
        Assert.True(staticRms < 0.055f, $"collision hiss should be audible but not painfully loud; RMS was {staticRms}");
        Assert.All(frame, sample => Assert.InRange(sample, -1f, 1f));
    }

    [Fact]
    public void Reset_restores_collision_effect_to_fresh_state_after_talkover_ends()
    {
        var dirty = new RadioCollisionDestructionModel(AudioEngine.SampleRate);
        var fresh = new RadioCollisionDestructionModel(AudioEngine.SampleRate);
        var warmup = Enumerable.Range(0, 320).Select(i => MathF.Sin(i * 0.04f) * 0.45f).ToArray();
        dirty.Process(warmup, active: true);

        dirty.Reset();

        var dirtyAfterReset = Enumerable.Range(0, 320).Select(i => MathF.Sin(i * 0.07f) * 0.5f).ToArray();
        var freshOutput = dirtyAfterReset.ToArray();
        dirty.Process(dirtyAfterReset, active: true);
        fresh.Process(freshOutput, active: true);

        Assert.Equal(freshOutput, dirtyAfterReset);
    }

    private static float Rms(float[] samples) =>
        MathF.Sqrt(samples.Sum(s => s * s) / samples.Length);

    private static int ZeroCrossings(float[] samples)
    {
        int count = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] < 0f && samples[i] >= 0f) ||
                (samples[i - 1] >= 0f && samples[i] < 0f))
            {
                count++;
            }
        }

        return count;
    }
}
