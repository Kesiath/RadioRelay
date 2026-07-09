using System;
using RadioRelay.Shared.Audio.Effects;

namespace RadioRelay.Shared.Audio
{
    /// 
    /// A complete radio "sound" -- txEffect (applied to your own mic before
    /// encoding), rxEffect (applied to decoded voice on the way out to
    /// speakers), an encryptionEffect (a purely cosmetic CVSD-style
    /// robotic/gritty character layered on top when a passcode is set --
    /// separate from this app's real AES-256-GCM encryption, which stays in
    /// effect regardless), and a noiseGain controlling how loud the
    /// recorded background static loop for that band is mixed under
    /// received audio. Structured directly after the DCS-SRS effect-graph
    /// schema (chain/filters/saturation/sidechainCompressor/gain) so the
    /// whole radio "sound" is just numbers, not bespoke code -- every value
    /// below can be retuned without touching the effect classes themselves.
    /// 
    public class RadioEffectProfile
    {
        public IAudioEffect TxEffect { get; }
        public IAudioEffect RxEffect { get; }
        public IAudioEffect EncryptionEffect { get; }

        /// Linear amplitude (already converted from dB) to mix the
        /// band's recorded background static loop under received audio.
        public float NoiseGainLinear { get; }

        private RadioEffectProfile(IAudioEffect tx, IAudioEffect rx, IAudioEffect encryption, float noiseGainDb)
        {
            TxEffect = tx;
            RxEffect = rx;
            EncryptionEffect = encryption;
            NoiseGainLinear = (float)Math.Pow(10, noiseGainDb / 20.0);
        }

        public static RadioEffectProfile ForBand(RadioBand band, bool isIntercom, int sampleRate)
        {
            if (isIntercom) return Intercom(sampleRate);
            return band switch
            {
                RadioBand.HF => Hf(sampleRate),
                RadioBand.UHF => Uhf(sampleRate),
                _ => Vhf(sampleRate)
            };
        }

        // VHF -- modeled directly on the reference DCS-SRS profile.
        private static RadioEffectProfile Vhf(int sr)
        {
            var tx = new ChainEffect(
                new ChainEffect(
                    new HighPassEffect(sr, 207, 0.5),
                    new PeakEffect(sr, 3112, 0.4, 16),
                    new LowPassEffect(sr, 6036, 0.4)),
                new SaturationEffect(gain: 2f, thresholdDb: -33),
                new SidechainCompressorEffect(sr, attackSeconds: 0.01f, releaseSeconds: 0.2f,
                    thresholdDb: -17, ratio: 1.18f, makeUpDb: -1,
                    sidechainEffect: new HighPassEffect(sr, 709)),
                new ChainEffect(
                    new HighPassEffect(sr, 393, 0.43),
                    new LowPassEffect(sr, 3692, 0.3)),
                new GainEffect(8));

            var rx = new ChainEffect(
                new HighPassEffect(sr, 270),
                new LowPassEffect(sr, 4500));

            return new RadioEffectProfile(tx, rx, new CvsdEncryptionEffect(), noiseGainDb: -60);
        }

        // HF -- narrower/tinnier band, more grit, noisier floor (real HF
        // propagation is inherently noisier than VHF/UHF line-of-sight).
        private static RadioEffectProfile Hf(int sr)
        {
            var tx = new ChainEffect(
                new ChainEffect(
                    new HighPassEffect(sr, 300, 0.5),
                    new PeakEffect(sr, 2200, 0.5, 14),
                    new LowPassEffect(sr, 2900, 0.4)),
                new SaturationEffect(gain: 3f, thresholdDb: -28),
                new SidechainCompressorEffect(sr, attackSeconds: 0.008f, releaseSeconds: 0.25f,
                    thresholdDb: -15, ratio: 1.35f, makeUpDb: -1,
                    sidechainEffect: new HighPassEffect(sr, 600)),
                new ChainEffect(
                    new HighPassEffect(sr, 350, 0.4),
                    new LowPassEffect(sr, 2600, 0.3)),
                new GainEffect(9));

            var rx = new ChainEffect(
                new HighPassEffect(sr, 300),
                new LowPassEffect(sr, 3000));

            return new RadioEffectProfile(tx, rx, new CvsdEncryptionEffect(), noiseGainDb: -42);
        }

        // UHF -- wider/cleaner band, lighter grit, quieter floor (typical
        // line-of-sight military air-band character).
        private static RadioEffectProfile Uhf(int sr)
        {
            var tx = new ChainEffect(
                new ChainEffect(
                    new HighPassEffect(sr, 180, 0.5),
                    new PeakEffect(sr, 3400, 0.35, 13),
                    new LowPassEffect(sr, 6800, 0.4)),
                new SaturationEffect(gain: 1.6f, thresholdDb: -36),
                new SidechainCompressorEffect(sr, attackSeconds: 0.012f, releaseSeconds: 0.18f,
                    thresholdDb: -19, ratio: 1.12f, makeUpDb: -1,
                    sidechainEffect: new HighPassEffect(sr, 800)),
                new ChainEffect(
                    new HighPassEffect(sr, 350, 0.4),
                    new LowPassEffect(sr, 4200, 0.3)),
                new GainEffect(7));

            var rx = new ChainEffect(
                new HighPassEffect(sr, 240),
                new LowPassEffect(sr, 5200));

            return new RadioEffectProfile(tx, rx, new CvsdEncryptionEffect(), noiseGainDb: -66);
        }

        // Intercom -- much lighter touch: barely any coloration, no
        // compression stage, near-silent background floor.
        private static RadioEffectProfile Intercom(int sr)
        {
            var tx = new ChainEffect(
                new ChainEffect(
                    new HighPassEffect(sr, 120, 0.7),
                    new LowPassEffect(sr, 7000, 0.7)),
                new SaturationEffect(gain: 1.15f, thresholdDb: -50),
                new GainEffect(2));

            var rx = new ChainEffect(
                new HighPassEffect(sr, 100),
                new LowPassEffect(sr, 7500));

            return new RadioEffectProfile(tx, rx, new CvsdEncryptionEffect(), noiseGainDb: -80);
        }
    }
}
