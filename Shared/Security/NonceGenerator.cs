using System;
using System.Security.Cryptography;

namespace RadioRelay.Shared.Security
{
    /// 
    /// Produces 12-byte GCM nonces that are guaranteed unique for the
    /// lifetime of this generator: a random 4-byte prefix picked once, plus
    /// an 8-byte counter that increments every call. Reusing a nonce with
    /// the same AES-GCM key breaks its security guarantees, so one of these
    /// should be created per net key in use, not shared across keys.
    /// 
    public class NonceGenerator
    {
        private readonly byte[] _prefix = new byte[4];
        private ulong _counter;

        public NonceGenerator()
        {
            RandomNumberGenerator.Fill(_prefix);
        }

        public byte[] Next()
        {
            var nonce = new byte[12];
            Array.Copy(_prefix, nonce, 4);
            var counterBytes = BitConverter.GetBytes(_counter++);
            if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);
            Array.Copy(counterBytes, 0, nonce, 4, 8);
            return nonce;
        }
    }
}
