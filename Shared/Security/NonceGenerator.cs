using System;
using System.Security.Cryptography;

namespace RadioRelay.Shared.Security
{
    /// <summary>
    /// Generates unique 12-byte GCM nonces from a random process prefix and
    /// monotonic counter. Use one generator per encryption key.
    /// </summary>
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
