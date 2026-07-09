using System;

namespace RadioRelay.Shared.Audio.Effects
{
    /// DCS-SRS "$type": "cvsd" -- a cosmetic simulation of a
    /// Continuously Variable Slope Delta modulator, the kind of coarse
    /// 1-bit-per-sample codec real secure/encrypted radios historically
    /// used. This is purely a SOUND CHARACTER effect (gives encrypted
    /// transmissions a distinct gritty/robotic quality) and has nothing to
    /// do with this app's actual security -- that's handled separately by
    /// real AES-256-GCM encryption regardless of whether this effect is
    /// applied. Coarsens the signal into fixed-size up/down steps rather
    /// than reproducing it exactly, which is what produces the
    /// characteristic buzzy quantization texture.
    public class CvsdEncryptionEffect : IAudioEffect
    {
        private const int Levels = 128;
        public void Process(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = MathF.Round(samples[i] * Levels) / Levels;
            }
        }
    }
}
