using NAudio.Wave;
using RadioRelay.Client;
using RadioRelay.Client.AudioEngineNs;
using RadioRelay.Shared.Audio;
using RadioRelay.Shared.Audio.Effects;

namespace RadioRelay.Tests;

public class RadioEffectsRealismTests
{
    [Fact]
    public void Fm_profile_remains_mandatory_and_has_no_strength_bypass()
    {
        Assert.Null(typeof(AudioEngine).GetProperty("RadioEffectStrength"));
        Assert.Null(typeof(AppSettings).GetProperty("RadioEffectStrength"));
        Assert.NotNull(typeof(RadioEffectProfile).GetProperty("EncryptionEffect"));
    }

    [Fact]
    public void Hf_vhf_and_uhf_have_distinct_fixed_signal_chains()
    {
        var input = VoiceLikeSignal(3200);
        var hf = Cook(input, RadioBand.HF);
        var vhf = Cook(input, RadioBand.VHF);
        var uhf = Cook(input, RadioBand.UHF);

        Assert.True(RmsDifference(hf, vhf) > 0.02f);
        Assert.True(RmsDifference(vhf, uhf) > 0.02f);
        Assert.True(RmsDifference(hf, uhf) > 0.03f);
    }

    [Fact]
    public void Hf_has_more_audible_presence_edge_than_vhf()
    {
        float hfBody = CookedToneRms(RadioBand.HF, 850f);
        float hfEdge = CookedToneRms(RadioBand.HF, 2100f);
        float vhfBody = CookedToneRms(RadioBand.VHF, 850f);
        float vhfEdge = CookedToneRms(RadioBand.VHF, 2100f);

        float hfEdgeRatio = hfEdge / hfBody;
        float vhfEdgeRatio = vhfEdge / vhfBody;

        Assert.True(hfEdgeRatio > vhfEdgeRatio * 1.05f,
            $"hfEdge={hfEdgeRatio:F3}, vhfEdge={vhfEdgeRatio:F3}");
    }

    [Fact]
    public void Vhf_is_mid_forward_while_uhf_retains_more_upper_voice_detail()
    {
        float vhfMid = CookedToneRms(RadioBand.VHF, 2050f);
        float uhfMid = CookedToneRms(RadioBand.UHF, 2050f);
        float vhfBody = CookedToneRms(RadioBand.VHF, 900f);
        float uhfBody = CookedToneRms(RadioBand.UHF, 900f);
        float vhfUpper = CookedToneRms(RadioBand.VHF, 3400f);
        float uhfUpper = CookedToneRms(RadioBand.UHF, 3400f);

        float vhfPresenceRatio = vhfMid / vhfBody;
        float uhfPresenceRatio = uhfMid / uhfBody;
        float vhfUpperRatio = vhfUpper / vhfMid;
        float uhfUpperRatio = uhfUpper / uhfMid;

        Assert.True(vhfPresenceRatio > uhfPresenceRatio * 1.10f,
            $"vhfPresence={vhfPresenceRatio:F3}, uhfPresence={uhfPresenceRatio:F3}");
        Assert.True(uhfUpperRatio > vhfUpperRatio * 1.50f,
            $"vhfUpper={vhfUpperRatio:F3}, uhfUpper={uhfUpperRatio:F3}");
    }

    [Fact]
    public void Band_noise_is_distinct_and_follows_fixed_hf_vhf_uhf_floor_order()
    {
        var generator = new RadioNoiseGenerator();
        var hf = new float[3200];
        var vhf = new float[3200];
        var uhf = new float[3200];

        generator.AddTo(hf, RadioEffectProfile.ForBand(RadioBand.HF, false, 16000));
        generator.Reset();
        generator.AddTo(vhf, RadioEffectProfile.ForBand(RadioBand.VHF, false, 16000));
        generator.Reset();
        generator.AddTo(uhf, RadioEffectProfile.ForBand(RadioBand.UHF, false, 16000));

        Assert.True(Rms(hf) > Rms(vhf) * 1.25f);
        Assert.True(Rms(vhf) > Rms(uhf) * 1.25f);
        Assert.True(RmsDifference(hf, vhf) > 0.001f);
        Assert.True(RmsDifference(vhf, uhf) > 0.001f);
    }

    [Fact]
    public void Cvsd_coloration_is_noticeable_but_preserves_voice_intelligibility()
    {
        var dry = VoiceLikeSignal(3200);
        var profile = RadioEffectProfile.ForBand(RadioBand.VHF, false, 16000);
        profile.TxEffect.Process(dry);
        var colored = (float[])dry.Clone();
        var reset = (float[])dry.Clone();

        profile.EncryptionEffect.Process(colored);
        profile.ResetTransmit();
        profile.EncryptionEffect.Process(reset);

        float difference = RmsDifference(dry, colored);
        Assert.True(difference > 0.005f);
        Assert.True(difference < Rms(dry) * 0.30f);
        Assert.True(Correlation(dry, colored) > 0.95f);
        Assert.Equal(colored, reset);
    }

    [Fact]
    public void Production_encrypted_transmit_order_is_cvsd_then_fm_modulation()
    {
        const uint seed = 0x1432ABCDu;
        var input = VoiceLikeSignal(3200);
        for (int i = 0; i < input.Length; i++) input[i] *= 0.55f;

        var actual = (float[])input.Clone();
        var expected = (float[])input.Clone();
        var wrongOrder = (float[])input.Clone();
        var actualProfile = RadioEffectProfile.ForBand(RadioBand.VHF, false, 16000);
        var expectedProfile = RadioEffectProfile.ForBand(RadioBand.VHF, false, 16000);
        var wrongProfile = RadioEffectProfile.ForBand(RadioBand.VHF, false, 16000);
        actualProfile.ResetTransmit(seed);
        expectedProfile.ResetTransmit(seed);
        wrongProfile.ResetTransmit(seed);

        AudioEngine.ApplyTransmitEffects(actual, actualProfile, encrypted: true);
        expectedProfile.EncryptionEffect.Process(expected);
        expectedProfile.TxEffect.Process(expected);
        wrongProfile.TxEffect.Process(wrongOrder);
        wrongProfile.EncryptionEffect.Process(wrongOrder);

        Assert.Equal(expected, actual);
        Assert.True(RmsDifference(actual, wrongOrder) > 0.001f);
        Assert.True(Rms(actual) > 0.02f);
    }

    [Fact]
    public void Centered_receiver_is_untouched_but_edge_detuning_narrows_fm_audio()
    {
        var original = ToneSignal(3000f, 0.20f, 3200);
        var centered = (float[])original.Clone();
        var edge = (float[])original.Clone();
        var centeredEffect = new ReceiverDetuningEffect();
        var edgeEffect = new ReceiverDetuningEffect();

        centeredEffect.Reset(0f, RadioBand.VHF, isIntercom: false, transmissionSeed: 123u);
        edgeEffect.Reset(0.0049f, RadioBand.VHF, isIntercom: false, transmissionSeed: 123u);
        centeredEffect.Process(centered);
        edgeEffect.Process(edge);

        Assert.Equal(original, centered);
        Assert.True(Rms(edge.AsSpan(edge.Length / 2)) < Rms(centered.AsSpan(centered.Length / 2)) * 0.70f);
        Assert.True(RmsDifference(centered, edge) > 0.03f);
    }

    [Fact]
    public void Receiver_detuning_texture_is_seeded_and_repeatable()
    {
        var first = ToneSignal(1800f, 0.12f, 1280);
        var repeated = (float[])first.Clone();
        var different = (float[])first.Clone();
        var firstEffect = new ReceiverDetuningEffect();
        var repeatedEffect = new ReceiverDetuningEffect();
        var differentEffect = new ReceiverDetuningEffect();

        firstEffect.Reset(0.004f, RadioBand.UHF, false, 77u);
        repeatedEffect.Reset(0.004f, RadioBand.UHF, false, 77u);
        differentEffect.Reset(0.004f, RadioBand.UHF, false, 78u);
        firstEffect.Process(first);
        repeatedEffect.Process(repeated);
        differentEffect.Process(different);

        Assert.Equal(first, repeated);
        Assert.True(RmsDifference(first, different) > 0.0001f);
    }

    [Fact]
    public void Fm_transmit_chain_strongly_prioritizes_the_voice_band()
    {
        float low = ProcessedToneRms(120f);
        float voice = ProcessedToneRms(1800f);
        float high = ProcessedToneRms(6200f);

        Assert.True(voice > low * 2f, $"voice={voice:F4}, low={low:F4}");
        Assert.True(voice > high * 2f, $"voice={voice:F4}, high={high:F4}");
    }

    [Fact]
    public void Vhf_compander_gives_consonant_presence_more_weight_than_vowel_body()
    {
        float vowel = ProcessedToneRms(800f, amplitude: 0.04f);
        float consonant = ProcessedToneRms(2600f, amplitude: 0.04f);

        Assert.True(consonant > vowel * 1.20f, $"consonant={consonant:F4}, vowel={vowel:F4}");
    }

    [Fact]
    public void Vhf_compander_reduces_voice_dynamic_range()
    {
        float quiet = ProcessedToneRms(800f, amplitude: 0.02f);
        float loud = ProcessedToneRms(800f, amplitude: 0.20f);

        Assert.True(loud / quiet < 4f, $"quiet={quiet:F4}, loud={loud:F4}");
    }

    [Fact]
    public void Fm_chain_materially_colors_normal_voice_without_hard_clipping()
    {
        var dry = VoiceLikeSignal(3200);
        var cooked = (float[])dry.Clone();
        var profile = RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, 16000);

        profile.TxEffect.Process(cooked);
        profile.RxEffect.Process(cooked);

        Assert.True(RmsDifference(dry, cooked) > 0.08f);
        Assert.True(cooked.Max(sample => Math.Abs(sample)) <= 1f);
        Assert.True(cooked.Count(sample => Math.Abs(sample) >= 0.999f) < cooked.Length / 100);
    }

    [Fact]
    public void Stateful_fm_chain_reset_reproduces_the_same_audio()
    {
        var input = VoiceLikeSignal(1280);
        var first = (float[])input.Clone();
        var reset = (float[])input.Clone();
        var profile = RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, 16000);

        profile.TxEffect.Process(first);
        profile.ResetTransmit();
        profile.TxEffect.Process(reset);

        Assert.Equal(first, reset);
    }

    [Fact]
    public void Procedural_fm_noise_is_repeatable_after_reset_but_not_a_short_loop()
    {
        var profile = RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, 16000);
        var generator = new RadioNoiseGenerator();
        var first = new float[320];
        var continued = new float[320];
        var reset = new float[320];

        generator.AddTo(first, profile);
        generator.AddTo(continued, profile);
        generator.Reset();
        generator.AddTo(reset, profile);

        Assert.Equal(first, reset);
        Assert.True(RmsDifference(first, continued) > 0.0001f);
    }

    [Fact]
    public void Authored_hf_vhf_and_uhf_noise_beds_are_present_as_secondary_layers()
    {
        Assert.NotEmpty(SoundLibrary.GetBandNoiseLoop(RadioBand.HF));
        Assert.NotEmpty(SoundLibrary.GetBandNoiseLoop(RadioBand.VHF));
        Assert.NotEmpty(SoundLibrary.GetBandNoiseLoop(RadioBand.UHF));

        foreach (var band in new[] { RadioBand.HF, RadioBand.VHF, RadioBand.UHF })
        {
            var profile = RadioEffectProfile.ForBand(band, isIntercom: false, 16000);
            Assert.True(profile.AuthoredNoiseGainLinear > 0f);
            Assert.True(profile.AuthoredNoiseGainLinear < profile.NoiseGainLinear);
        }
    }

    [Fact]
    public void Transmission_noise_seed_is_repeatable_and_varies_between_transmissions()
    {
        var profile = RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, 16000);
        var first = new float[640];
        var repeated = new float[640];
        var different = new float[640];

        new RadioNoiseGenerator(123u).AddTo(first, profile);
        new RadioNoiseGenerator(123u).AddTo(repeated, profile);
        new RadioNoiseGenerator(456u).AddTo(different, profile);

        Assert.Equal(first, repeated);
        Assert.True(RmsDifference(first, different) > 0.0001f);
    }

    [Fact]
    public void Tuned_frequency_continuously_shapes_static_inside_a_radio_band()
    {
        var profile = RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, 16000);
        var lowVhf = new float[6400];
        var repeatedLowVhf = new float[6400];
        var highVhf = new float[6400];

        new RadioNoiseGenerator(123u).AddTo(lowVhf, profile, 40f);
        new RadioNoiseGenerator(123u).AddTo(repeatedLowVhf, profile, 40f);
        new RadioNoiseGenerator(123u).AddTo(highVhf, profile, 290f);

        Assert.Equal(lowVhf, repeatedLowVhf);
        Assert.True(RmsDifference(lowVhf, highVhf) > 0.0001f);
        Assert.True(NormalizedRoughness(highVhf) > NormalizedRoughness(lowVhf) * 1.03f,
            $"low={NormalizedRoughness(lowVhf):F3}, high={NormalizedRoughness(highVhf):F3}");
    }

    [Theory]
    [InlineData(RadioBand.HF, 15f)]
    [InlineData(RadioBand.VHF, 165f)]
    [InlineData(RadioBand.UHF, 650f)]
    public void Receiver_static_is_dark_coloured_noise_instead_of_high_white_hiss(
        RadioBand band,
        float frequencyMHz)
    {
        var noise = new float[6400];
        var profile = RadioEffectProfile.ForBand(band, isIntercom: false, 16000);

        new RadioNoiseGenerator(0xA341316Cu).AddTo(noise, profile, frequencyMHz);

        Assert.True(NormalizedRoughness(noise) < 1.10f,
            $"band={band}, roughness={NormalizedRoughness(noise):F3}");
    }

    [Fact]
    public void Shared_receive_path_uses_the_exact_tuned_frequency_for_static()
    {
        var lowChannel = new RadioRelay.Client.Radio.RadioChannel { Frequency = 40f };
        var highChannel = new RadioRelay.Client.Radio.RadioChannel { Frequency = 290f };
        var lowProfile = RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, 16000);
        var highProfile = RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, 16000);

        var low = RadioReceiveFrameProcessor.Process(
            decodedPcm: null,
            lowChannel,
            lowProfile,
            new RadioNoiseGenerator(123u));
        var high = RadioReceiveFrameProcessor.Process(
            decodedPcm: null,
            highChannel,
            highProfile,
            new RadioNoiseGenerator(123u));

        Assert.True(RmsDifference(low, high) > 0.0001f);
    }

    [Fact]
    public void Fm_emphasis_has_the_standard_communications_slope_and_inverse()
    {
        float preLow = EffectToneRms(new FmEmphasisEffect(16000, preEmphasis: true), 500f);
        float preHigh = EffectToneRms(new FmEmphasisEffect(16000, preEmphasis: true), 2500f);
        float deLow = EffectToneRms(new FmEmphasisEffect(16000, preEmphasis: false), 500f);
        float deHigh = EffectToneRms(new FmEmphasisEffect(16000, preEmphasis: false), 2500f);

        Assert.True(preHigh > preLow * 3f, $"preLow={preLow:F4}, preHigh={preHigh:F4}");
        Assert.True(deLow > deHigh * 3f, $"deLow={deLow:F4}, deHigh={deHigh:F4}");

        var original = VoiceLikeSignal(3200);
        var roundTrip = (float[])original.Clone();
        new FmEmphasisEffect(16000, preEmphasis: true).Process(roundTrip);
        new FmEmphasisEffect(16000, preEmphasis: false).Process(roundTrip);

        Assert.True(RmsDifference(original, roundTrip) < 0.0001f);
    }

    [Fact]
    public void Deviation_limiter_matches_the_fixed_25khz_fm_reference_level()
    {
        var referenceTone = ToneSignal(
            1000f,
            MathF.Pow(10f, FmDeviationLimiterEffect.ReferenceToneDbov / 20f),
            1600);
        var overdriven = ToneSignal(1000f, 0.20f, 1600);
        var limiter = new FmDeviationLimiterEffect();

        limiter.Process(referenceTone);
        limiter.Process(overdriven);

        Assert.InRange(referenceTone.Max(Math.Abs), 0.599f, 0.601f);
        Assert.Equal(1f, overdriven.Max(Math.Abs));
        Assert.Equal(5000f, FmDeviationLimiterEffect.MaximumDeviationHz);
    }

    [Fact]
    public void Seeded_transmitter_tolerance_is_repeatable_subtle_and_voice_safe()
    {
        var input = VoiceLikeSignal(3200);
        for (int i = 0; i < input.Length; i++) input[i] *= 0.15f;
        var first = (float[])input.Clone();
        var repeated = (float[])input.Clone();
        var different = (float[])input.Clone();
        var profile = RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, 16000);

        profile.ResetTransmit(123u);
        profile.TxEffect.Process(first);
        profile.ResetTransmit(123u);
        profile.TxEffect.Process(repeated);
        profile.ResetTransmit(456u);
        profile.TxEffect.Process(different);

        Assert.Equal(first, repeated);
        Assert.True(RmsDifference(first, different) > 0.0001f);
        Assert.True(Correlation(first, different) > 0.98f);
    }

    [Fact]
    public void Fm_reference_stages_are_causal_and_add_no_lookahead()
    {
        var impulse = new float[320];
        impulse[0] = 0.02f;
        var chain = new ChainEffect(
            new FmEmphasisEffect(16000, preEmphasis: true),
            new FmDeviationLimiterEffect(),
            new FmEmphasisEffect(16000, preEmphasis: false));

        chain.Process(impulse);

        Assert.NotEqual(0f, impulse[0]);
    }

    [Fact]
    public void Output_limiter_is_zero_lookahead_and_stays_bounded()
    {
        var source = new ArraySampleProvider(Enumerable.Repeat(2f, 128).ToArray());
        var limiter = new SoftLimiterSampleProvider(source);
        var buffer = new float[128];

        int read = limiter.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, read);
        Assert.All(buffer, sample => Assert.InRange(sample, -1f, 1f));
        Assert.NotEqual(0f, buffer[0]);
    }

    [Fact]
    public void Receive_cues_use_the_same_full_length_filter_chain_as_voice()
    {
        const float frequency = 45.5f;
        const uint seed = 0x5A17C0DEu;
        var cue = SoundLibrary.RxStart.ToArray();
        var original = cue.ToArray();

        var expectedProfile = RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, AudioEngine.SampleRate);
        expectedProfile.ResetReceive();
        var expectedNoise = new RadioNoiseGenerator(seed);
        var expected = cue.ToArray();
        expectedNoise.AddTo(expected, expectedProfile, frequency);
        expectedProfile.RxEffect.Process(expected);

        var actualProfile = RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, AudioEngine.SampleRate);
        actualProfile.ResetReceive();
        var actual = RadioReceiveFrameProcessor.ProcessSamples(
            cue,
            frequency,
            isIntercom: false,
            actualProfile,
            new RadioNoiseGenerator(seed));

        Assert.Equal(original, cue);
        Assert.Equal(cue.Length, actual.Length);
        Assert.True(RmsDifference(cue, actual) > 0.005f);
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(expected[i] - actual[i]), 0f, 1e-6f);
    }

    private static float ProcessedToneRms(float frequency, float amplitude = 0.08f)
    {
        var samples = new float[3200];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = MathF.Sin(2f * MathF.PI * frequency * i / 16000f) * amplitude;

        RadioEffectProfile.ForBand(RadioBand.VHF, isIntercom: false, 16000)
            .TxEffect.Process(samples);
        return Rms(samples.AsSpan(samples.Length / 2));
    }

    private static float EffectToneRms(IAudioEffect effect, float frequency)
    {
        var samples = ToneSignal(frequency, 0.01f, 3200);
        effect.Process(samples);
        return Rms(samples.AsSpan(samples.Length / 2));
    }

    private static float[] ToneSignal(float frequency, float amplitude, int length)
    {
        var samples = new float[length];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = MathF.Sin(2f * MathF.PI * frequency * i / 16000f) * amplitude;
        return samples;
    }

    private static float[] Cook(float[] input, RadioBand band)
    {
        var samples = (float[])input.Clone();
        var profile = RadioEffectProfile.ForBand(band, isIntercom: false, 16000);
        profile.TxEffect.Process(samples);
        profile.RxEffect.Process(samples);
        return samples;
    }

    private static float CookedToneRms(RadioBand band, float frequency)
    {
        var samples = ToneSignal(frequency, 0.02f, 3200);
        var profile = RadioEffectProfile.ForBand(band, isIntercom: false, 16000);
        profile.TxEffect.Process(samples);
        profile.RxEffect.Process(samples);
        return Rms(samples.AsSpan(samples.Length / 2));
    }

    private static float[] VoiceLikeSignal(int length)
    {
        var samples = new float[length];
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / 16000f;
            samples[i] =
                MathF.Sin(2f * MathF.PI * 180f * t) * 0.10f +
                MathF.Sin(2f * MathF.PI * 920f * t) * 0.08f +
                MathF.Sin(2f * MathF.PI * 1850f * t) * 0.06f +
                MathF.Sin(2f * MathF.PI * 3400f * t) * 0.035f;
        }

        return samples;
    }

    private static float Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float sample in samples) sum += sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static float RmsDifference(float[] left, float[] right)
    {
        double sum = 0;
        for (int i = 0; i < left.Length; i++)
        {
            double difference = left[i] - right[i];
            sum += difference * difference;
        }

        return (float)Math.Sqrt(sum / left.Length);
    }

    private static float NormalizedRoughness(float[] samples)
    {
        double differenceEnergy = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            double difference = samples[i] - samples[i - 1];
            differenceEnergy += difference * difference;
        }

        float differenceRms = (float)Math.Sqrt(differenceEnergy / (samples.Length - 1));
        return differenceRms / Math.Max(1e-7f, Rms(samples));
    }

    private static float Correlation(float[] left, float[] right)
    {
        double dot = 0;
        double leftEnergy = 0;
        double rightEnergy = 0;
        for (int i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftEnergy += left[i] * left[i];
            rightEnergy += right[i] * right[i];
        }

        return (float)(dot / Math.Sqrt(leftEnergy * rightEnergy));
    }

    private sealed class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ArraySampleProvider(float[] samples)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
