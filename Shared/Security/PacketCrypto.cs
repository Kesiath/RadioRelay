using System.Security.Cryptography;

namespace RadioRelay.Shared.Security
{
    /// 
    /// AES-256-GCM authenticated encryption for a single Opus frame. GCM
    /// gives both confidentiality (nobody without the key can hear the
    /// audio) and integrity (a tampered/corrupted packet fails to decrypt
    /// rather than producing garbled audio or being silently accepted).
    /// 
    public static class PacketCrypto
    {
        public const int NonceSize = 12;
        public const int TagSize = 16;

        public static (byte[] ciphertext, byte[] tag) Encrypt(byte[] key, byte[] nonce, byte[] plaintext)
        {
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            return (ciphertext, tag);
        }

        /// Returns null if authentication fails (wrong key, or the
        /// packet was corrupted/tampered with) rather than throwing, so
        /// callers can just treat it as "can't hear this transmission".
        public static byte[]? Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
        {
            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
                return plaintext;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }
}
