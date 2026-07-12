using RadioRelay.Client.AudioEngineNs;

namespace RadioRelay.Tests;

public class AudioDeviceEnumeratorTests
{
    [Fact]
    public void Truncated_winmm_names_are_replaced_with_full_core_audio_names()
    {
        var legacy = new[]
        {
            "SteelSeries Sonar - Gaming (Ste",
            "CABLE Input (VB-Audio Virtual C"
        };
        var full = new[]
        {
            "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)",
            "CABLE Input (VB-Audio Virtual Cable)"
        };

        var resolved = AudioDeviceEnumerator.ResolveFullNames(legacy, full);

        Assert.Equal(full, resolved);
    }

    [Fact]
    public void Unmatched_winmm_names_are_preserved()
    {
        var resolved = AudioDeviceEnumerator.ResolveFullNames(
            new[] { "Unmatched legacy device" },
            Array.Empty<string>());

        Assert.Equal("Unmatched legacy device", Assert.Single(resolved));
    }
}
