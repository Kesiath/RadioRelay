using System;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Applies deterministic receiver selectivity within the accepted 5 kHz tuning window.
    /// </summary>
    internal sealed class ReceiverDetuningEffect
    {
        internal const float CaptureWindowMHz = 0.005f;

        private FirstOrderFilter? _lowPass;
        private float _strength;
        private float _gain = 1f;
        private float _noiseMemory;
        private uint _randomState = 0x7F4A7C15u;

        public void Reset(
            float frequencyOffsetMHz,
            RadioBand band,
            bool isIntercom,
            uint transmissionSeed)
        {
            if (isIntercom)
            {
                _strength = 0f;
                _gain = 1f;
                _lowPass = null;
                _noiseMemory = 0f;
                return;
            }

            float normalized = Math.Clamp(
                Math.Abs(frequencyOffsetMHz) / CaptureWindowMHz,
                0f,
                1f);
            float active = Math.Clamp((normalized - 0.08f) / 0.92f, 0f, 1f);
            _strength = active * active * (3f - 2f * active);
            _gain = 1f - 0.20f * MathF.Pow(_strength, 1.25f);

            float centerCutoff = band switch
            {
                RadioBand.HF => 2700f,
                RadioBand.UHF => 3600f,
                _ => 3100f
            };
            float edgeCutoff = band switch
            {
                RadioBand.HF => 1650f,
                RadioBand.UHF => 2550f,
                _ => 2050f
            };
            float cutoff = centerCutoff + (edgeCutoff - centerCutoff) * _strength;
            _lowPass = FirstOrderFilter.LowPass(AudioEngine.SampleRate, cutoff);
            _noiseMemory = 0f;

            uint offsetBits = unchecked((uint)BitConverter.SingleToInt32Bits(frequencyOffsetMHz));
            _randomState = transmissionSeed ^ offsetBits ^ 0x7F4A7C15u;
            if (_randomState == 0) _randomState = 0x7F4A7C15u;
        }

        public void Process(float[] samples)
        {
            if (_strength <= 0f || _lowPass == null) return;

            float filteredMix = 0.78f * _strength;
            float noiseGain = 0.009f * MathF.Pow(_strength, 1.4f);
            for (int i = 0; i < samples.Length; i++)
            {
                float dry = samples[i];
                float filtered = _lowPass.Process(dry);
                float recovered = (dry + (filtered - dry) * filteredMix) * _gain;

                float white = NextSigned();
                _noiseMemory = _noiseMemory * 0.84f + white * 0.16f;
                float discriminatorNoise = (white - _noiseMemory * 0.55f) * noiseGain;
                samples[i] = Math.Clamp(recovered + discriminatorNoise, -1f, 1f);
            }
        }

        private float NextSigned()
        {
            _randomState ^= _randomState << 13;
            _randomState ^= _randomState >> 17;
            _randomState ^= _randomState << 5;
            return ((_randomState & 0x00FFFFFFu) / 8388607.5f) - 1f;
        }
    }
}
