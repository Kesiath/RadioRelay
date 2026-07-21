using RadioRelay.Client.AudioEngineNs;

namespace RadioRelay.Tests;

public class ApplicationAmbienceTests
{
    [Fact]
    public void Processor_resamples_and_downmixes_application_audio()
    {
        var processor = new ApplicationAmbienceProcessor();
        processor.WritePcm16Stereo(StereoTone(700f, 0.8f, 500));

        var output = processor.ReadSamples(320);

        Assert.Contains(output, sample => Math.Abs(sample) > 0.001f);
        Assert.InRange(Rms(output), 0.45, 0.65);
    }

    [Fact]
    public void Processor_preserves_consonant_band_for_the_shared_radio_chain()
    {
        var voiceBand = new ApplicationAmbienceProcessor();
        var consonantBand = new ApplicationAmbienceProcessor();
        voiceBand.WritePcm16Stereo(StereoTone(800f, 0.5f, 500));
        consonantBand.WritePcm16Stereo(StereoTone(3200f, 0.5f, 500));

        double voiceBandRms = Rms(voiceBand.ReadSamples(640));
        double consonantBandRms = Rms(consonantBand.ReadSamples(640));

        Assert.True(consonantBandRms > voiceBandRms * 0.7,
            $"Expected pickup to preserve consonants, got {consonantBandRms:F5} versus {voiceBandRms:F5}.");
    }

    [Fact]
    public void Processor_bounds_backlog_and_clears_pre_ptt_audio()
    {
        var processor = new ApplicationAmbienceProcessor();
        processor.WritePcm16Stereo(StereoTone(500f, 0.5f, 2000));

        int maximumSamples = AudioEngine.SampleRate *
            ApplicationAmbienceProcessor.MaximumBufferedMilliseconds / 1000;
        Assert.InRange(processor.BufferedSamples, 1, maximumSamples);

        processor.ResetTransmissionBuffer();

        Assert.Equal(0, processor.BufferedSamples);
        Assert.All(processor.ReadSamples(320), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Ambience_gain_is_independent_from_microphone_input_gain()
    {
        var microphone = new short[320];
        var ambience = Enumerable.Repeat(0.04f, 320).ToArray();

        var mutedMic = AudioEngine.PrepareTransmitSamples(microphone, ambience, 0f);
        var boostedMic = AudioEngine.PrepareTransmitSamples(microphone, ambience, 3f);

        Assert.All(mutedMic, sample => Assert.Equal(0.04f, sample, precision: 5));
        Assert.Equal(mutedMic, boostedMic);
    }

    [Fact]
    public void Ambience_bleed_gain_scales_application_input_before_radio_processing()
    {
        var microphone = new short[320];
        var ambience = Enumerable.Repeat(0.4f, 320).ToArray();

        var closed = AudioEngine.PrepareTransmitSamples(microphone, ambience, 1f, 0f);
        var half = AudioEngine.PrepareTransmitSamples(microphone, ambience, 1f, 0.5f);
        var open = AudioEngine.PrepareTransmitSamples(microphone, ambience, 1f, 1f);

        Assert.All(closed, sample => Assert.Equal(0f, sample));
        Assert.All(half, sample => Assert.Equal(0.2f, sample, precision: 5));
        Assert.All(open, sample => Assert.Equal(0.4f, sample, precision: 5));
    }

    [Fact]
    public void Application_identity_prefers_executable_path_case_insensitively()
    {
        var target = new ApplicationAudioTarget(
            42,
            "game",
            @"C:\Games\Example\game.exe",
            "Example Game");

        Assert.True(ApplicationAudioEnumerator.Matches(
            target,
            @"c:\games\example\GAME.EXE",
            "different-name"));
        Assert.False(ApplicationAudioEnumerator.Matches(
            target,
            @"C:\Games\Other\game.exe",
            "game"));
    }

    [Fact]
    public void Application_selection_prefers_the_process_that_owns_an_audio_session()
    {
        var visibleWindow = new ApplicationAudioTarget(
            20,
            "game",
            @"C:\Games\Example\game.exe",
            "Example Game");
        var audioProcess = new ApplicationAudioTarget(
            40,
            "game",
            @"C:\Games\Example\game.exe",
            "game",
            HasAudioSession: true,
            IsAudioActive: true);

        var selected = Assert.Single(ApplicationAudioEnumerator.SelectPreferredTargets(
            new[] { visibleWindow, audioProcess }));

        Assert.Equal(audioProcess.ProcessId, selected.ProcessId);
    }

    [Fact]
    public void Application_enumerator_excludes_radio_relay_itself()
    {
        if (!OperatingSystem.IsWindows()) return;

        var applications = ApplicationAudioEnumerator.GetRunningApplications();

        Assert.DoesNotContain(applications, item => item.ProcessId == Environment.ProcessId);
    }

    private static byte[] StereoTone(float frequency, float amplitude, int durationMilliseconds)
    {
        int frames = ApplicationAmbienceProcessor.CaptureSampleRate * durationMilliseconds / 1000;
        var pcm = new byte[frames * ApplicationAmbienceProcessor.CaptureChannels * sizeof(short)];
        for (int frame = 0; frame < frames; frame++)
        {
            short sample = (short)(Math.Sin(
                2 * Math.PI * frequency * frame / ApplicationAmbienceProcessor.CaptureSampleRate) *
                amplitude * short.MaxValue);
            int offset = frame * ApplicationAmbienceProcessor.CaptureChannels * sizeof(short);
            BitConverter.GetBytes(sample).CopyTo(pcm, offset);
            BitConverter.GetBytes(sample).CopyTo(pcm, offset + sizeof(short));
        }

        return pcm;
    }

    private static double Rms(float[] samples) =>
        Math.Sqrt(samples.Select(sample => sample * sample).Average());
}
