using System;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Derives a stable fallback seed when transmission noise metadata is absent.
    /// </summary>
    internal static class RadioTransmissionNoiseSeed
    {
        private const uint FnvOffset = 2166136261u;
        private const uint FnvPrime = 16777619u;

        public static uint Resolve(uint transmittedSeed, ReadOnlySpan<byte> opusPayload) =>
            transmittedSeed != 0 ? transmittedSeed : FromOpusPayload(opusPayload);

        public static uint FromOpusPayload(ReadOnlySpan<byte> opusPayload)
        {
            uint hash = FnvOffset;
            foreach (byte value in opusPayload)
            {
                hash ^= value;
                hash *= FnvPrime;
            }

            return hash == 0 ? FnvOffset : hash;
        }
    }
}
