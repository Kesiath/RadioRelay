using System;

namespace RadioRelay.Shared.Audio.Effects
{
    /// DCS-SRS "$type": "sidechainCompressor" -- compresses the
    /// main signal based on the envelope of a (usually filtered) COPY of
    /// it, rather than the main signal's own raw envelope. This is what
    /// gives real radios their characteristic "pumping"/ducking behavior
    /// tied to specific frequency content (e.g. compressing based on
    /// presence of high-frequency energy) rather than overall loudness.
    public class SidechainCompressorEffect : IAudioEffect
    {
        private readonly float _attackCoeff;
        private readonly float _releaseCoeff;
        private readonly float _thresholdLinear;
        private readonly float _thresholdDb;
        private readonly float _ratio;
        private readonly float _makeUpLinear;
        private readonly IAudioEffect _sidechainEffect;
        private float _envelope;

        public SidechainCompressorEffect(
            int sampleRate, float attackSeconds, float releaseSeconds,
            float thresholdDb, float ratio, float makeUpDb, IAudioEffect sidechainEffect)
        {
            _attackCoeff = (float)Math.Exp(-1.0 / (Math.Max(0.0001, attackSeconds) * sampleRate));
            _releaseCoeff = (float)Math.Exp(-1.0 / (Math.Max(0.0001, releaseSeconds) * sampleRate));
            _thresholdDb = thresholdDb;
            _thresholdLinear = DbToLinear(thresholdDb);
            _ratio = Math.Max(1f, ratio);
            _makeUpLinear = DbToLinear(makeUpDb);
            _sidechainEffect = sidechainEffect;
        }

        public void Process(float[] samples)
        {
            // Build the control signal from a COPY of the input, so
            // filtering it (e.g. a highpass to key off consonant energy)
            // doesn't itself alter what's actually heard -- only how much
            // the real signal gets compressed.
            var sidechain = (float[])samples.Clone();
            _sidechainEffect.Process(sidechain);

            for (int i = 0; i < samples.Length; i++)
            {
                float rectified = Math.Abs(sidechain[i]);
                _envelope = rectified > _envelope
                    ? _attackCoeff * _envelope + (1 - _attackCoeff) * rectified
                    : _releaseCoeff * _envelope + (1 - _releaseCoeff) * rectified;

                float gainReduction = 1f;
                if (_envelope > _thresholdLinear && _envelope > 1e-6f)
                {
                    float overDb = LinearToDb(_envelope) - _thresholdDb;
                    float compressedDb = overDb / _ratio;
                    float reductionDb = overDb - compressedDb;
                    gainReduction = DbToLinear(-reductionDb);
                }

                samples[i] = Math.Clamp(samples[i] * gainReduction * _makeUpLinear, -1f, 1f);
            }
        }

        private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);
        private static float LinearToDb(float linear) => 20f * (float)Math.Log10(Math.Max(1e-9f, linear));
    }
}
