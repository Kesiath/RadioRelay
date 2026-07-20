using System;
using RadioRelay.Shared.Audio.Effects;

namespace RadioRelay.Shared.Audio
{
    /// <summary>
    /// Defines mandatory FM transmit, receive, encryption, and band-noise processing.
    /// </summary>
    public sealed class RadioEffectProfile
    {
        public IAudioEffect TxEffect { get; }
        public IAudioEffect RxEffect { get; }
        public IAudioEffect EncryptionEffect { get; }
        public RadioBand Band { get; }
        public float NoiseGainLinear { get; }
        public float AuthoredNoiseGainLinear { get; }
        private readonly TransmissionHardwareVariationEffect? _transmitVariation;

        private RadioEffectProfile(
            IAudioEffect tx,
            IAudioEffect rx,
            RadioBand band,
            float noiseGainDb,
            float authoredNoiseGainDb,
            TransmissionHardwareVariationEffect? transmitVariation = null)
        {
            TxEffect = tx;
            RxEffect = rx;
            EncryptionEffect = new CvsdEncryptionEffect();
            Band = band;
            NoiseGainLinear = MathF.Pow(10f, noiseGainDb / 20f);
            AuthoredNoiseGainLinear = MathF.Pow(10f, authoredNoiseGainDb / 20f);
            _transmitVariation = transmitVariation;
        }

        public void ResetTransmit(uint transmissionSeed = 0)
        {
            TxEffect.Reset();
            _transmitVariation?.Reset(transmissionSeed);
            EncryptionEffect.Reset();
        }

        public void ResetReceive() => RxEffect.Reset();

        public static RadioEffectProfile ForBand(RadioBand band, bool isIntercom, int sampleRate) =>
            isIntercom ? BuildIntercom(sampleRate, band) : BuildFm(sampleRate, band);

        private static RadioEffectProfile BuildFm(int sr, RadioBand band) => band switch
        {
            RadioBand.HF => BuildHfFm(sr),
            RadioBand.UHF => BuildUhfFm(sr),
            _ => BuildVhfFm(sr)
        };

        private static RadioEffectProfile BuildHfFm(int sr)
        {
            var variation = new TransmissionHardwareVariationEffect(sr);
            var tx = new ChainEffect(
                variation,
                new FirstOrderHighPassEffect(sr, 430f),
                new FirstOrderLowPassEffect(sr, 2850f),
                new SidechainCompressorEffect(
                    sr, 0.002f, 0.18f, -32f, 6f, 5.5f,
                    new FirstOrderLowPassEffect(sr, 1100f)),
                new PeakEffect(sr, 1750f, 0.78, 6f),
                new PeakEffect(sr, 2400f, 0.65, 4f),
                new FmEmphasisEffect(sr, preEmphasis: true),
                new FmDeviationLimiterEffect(),
                new PeakEffect(sr, 2100f, 0.75, 9f),
                new HighPassEffect(sr, 470f, 0.50),
                new LowPassEffect(sr, 2700f, 0.50),
                new GainEffect(-6.25f));

            var rx = new ChainEffect(
                new FmEmphasisEffect(sr, preEmphasis: false),
                new FirstOrderHighPassEffect(sr, 460f),
                new FirstOrderLowPassEffect(sr, 2800f),
                new SidechainCompressorEffect(
                    sr, 0.003f, 0.20f, -24f, 2.8f, 1.5f,
                    new FirstOrderLowPassEffect(sr, 1200f)),
                new PeakEffect(sr, 1950f, 0.82, 6f),
                new PeakEffect(sr, 2450f, 0.68, 2.5f),
                new SaturationEffect(gainDb: 3.25f, thresholdDb: -10.5f),
                new LowPassEffect(sr, 2700f, 0.54),
                new GainEffect(-4f));

            return new RadioEffectProfile(
                tx, rx, RadioBand.HF,
                noiseGainDb: -40f,
                authoredNoiseGainDb: -47f,
                transmitVariation: variation);
        }

        private static RadioEffectProfile BuildVhfFm(int sr)
        {
            var variation = new TransmissionHardwareVariationEffect(sr);
            var tx = new ChainEffect(
                variation,
                new FirstOrderHighPassEffect(sr, 350f),
                new FirstOrderLowPassEffect(sr, 3200f),
                new SidechainCompressorEffect(
                    sr, 0.0015f, 0.13f, -31f, 5f, 5f,
                    new FirstOrderLowPassEffect(sr, 1300f)),
                new PeakEffect(sr, 2100f, 0.72, 5.5f),
                new PeakEffect(sr, 2850f, 0.62, 3f),
                new FmEmphasisEffect(sr, preEmphasis: true),
                new FmDeviationLimiterEffect(),
                new PeakEffect(sr, 2050f, 0.80, 6f),
                new HighPassEffect(sr, 380f, 0.52),
                new LowPassEffect(sr, 3050f, 0.58),
                new GainEffect(-5f));

            var rx = new ChainEffect(
                new FmEmphasisEffect(sr, preEmphasis: false),
                new FirstOrderHighPassEffect(sr, 380f),
                new FirstOrderLowPassEffect(sr, 3100f),
                new SidechainCompressorEffect(
                    sr, 0.0025f, 0.16f, -25f, 2.9f, 1.75f,
                    new FirstOrderLowPassEffect(sr, 1400f)),
                new PeakEffect(sr, 2050f, 0.82, 3.5f),
                new SaturationEffect(gainDb: 2.25f, thresholdDb: -8.75f),
                new LowPassEffect(sr, 2925f, 0.58),
                new GainEffect(-3.25f));

            return new RadioEffectProfile(
                tx, rx, RadioBand.VHF,
                noiseGainDb: -48f,
                authoredNoiseGainDb: -57f,
                transmitVariation: variation);
        }

        private static RadioEffectProfile BuildUhfFm(int sr)
        {
            var variation = new TransmissionHardwareVariationEffect(sr);
            var tx = new ChainEffect(
                variation,
                new FirstOrderHighPassEffect(sr, 285f),
                new FirstOrderLowPassEffect(sr, 3800f),
                new SidechainCompressorEffect(
                    sr, 0.0015f, 0.10f, -29f, 3.5f, 3.5f,
                    new FirstOrderLowPassEffect(sr, 1500f)),
                new PeakEffect(sr, 2400f, 0.68, 0.5f),
                new PeakEffect(sr, 3350f, 0.62, 3f),
                new FmEmphasisEffect(sr, preEmphasis: true),
                new FmDeviationLimiterEffect(),
                new HighPassEffect(sr, 330f, 0.56),
                new LowPassEffect(sr, 3650f, 0.68),
                new GainEffect(-4.25f));

            var rx = new ChainEffect(
                new FmEmphasisEffect(sr, preEmphasis: false),
                new FirstOrderHighPassEffect(sr, 300f),
                new FirstOrderLowPassEffect(sr, 3750f),
                new SidechainCompressorEffect(
                    sr, 0.002f, 0.11f, -22f, 1.8f, 0.5f,
                    new FirstOrderLowPassEffect(sr, 1600f)),
                new PeakEffect(sr, 2700f, 0.72, 0.5f),
                new PeakEffect(sr, 3450f, 0.68, 2f),
                new SaturationEffect(gainDb: 0.75f, thresholdDb: -6f),
                new LowPassEffect(sr, 3600f, 0.70),
                new GainEffect(-2.25f));

            return new RadioEffectProfile(
                tx, rx, RadioBand.UHF,
                noiseGainDb: -56f,
                authoredNoiseGainDb: -64f,
                transmitVariation: variation);
        }

        private static RadioEffectProfile BuildIntercom(int sr, RadioBand band)
        {
            var tx = new ChainEffect(
                new FirstOrderHighPassEffect(sr, 120f),
                new FirstOrderLowPassEffect(sr, 7000f),
                new SaturationEffect(gainDb: 1.5f, thresholdDb: -10f));

            var rx = new ChainEffect(
                new FirstOrderHighPassEffect(sr, 100f),
                new FirstOrderLowPassEffect(sr, 7200f));

            return new RadioEffectProfile(
                tx, rx, band,
                noiseGainDb: -120f,
                authoredNoiseGainDb: -120f);
        }
    }
}
