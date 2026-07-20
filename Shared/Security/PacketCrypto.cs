using System;
using System.Security.Cryptography;
using System.Text;

namespace RadioRelay.Shared.Security
{
    /// <summary>
    /// Encrypts Opus frames with AES-GCM and authenticates audio headers with
    /// a domain-separated HMAC.
    /// </summary>
    public static class PacketCrypto
    {
        public const int NonceSize = 12;
        public const int TagSize = 16;
        public const int HeaderAuthenticationTagSize = 16;

        private static readonly byte[] HeaderAuthenticationSalt =
            Encoding.ASCII.GetBytes("RadioRelay-HeaderAuth-Salt-v1");
        private static readonly byte[] HeaderAuthenticationInfo =
            Encoding.ASCII.GetBytes("RadioRelay-AudioHeader-HMAC-SHA256-v1");

        public static (byte[] ciphertext, byte[] tag) Encrypt(byte[] key, byte[] nonce, byte[] plaintext)
            => Encrypt(key, nonce, plaintext, Array.Empty<byte>());

        /// <summary>
        /// Encrypts a payload with authenticated associated data.
        /// </summary>
        public static (byte[] ciphertext, byte[] tag) Encrypt(
            byte[] key,
            byte[] nonce,
            byte[] plaintext,
            byte[] associatedData)
        {
            ValidateKey(key);
            ValidateNonce(nonce);
            ArgumentNullException.ThrowIfNull(plaintext);
            ArgumentNullException.ThrowIfNull(associatedData);

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return (ciphertext, tag);
        }

        /// <summary>
        /// Decrypts a payload, returning null when authentication fails.
        /// </summary>
        public static byte[]? Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
            => Decrypt(key, nonce, ciphertext, tag, Array.Empty<byte>());

        /// <summary>
        /// Decrypts a payload with associated data, returning null for invalid
        /// input or failed authentication.
        /// </summary>
        public static byte[]? Decrypt(
            byte[] key,
            byte[] nonce,
            byte[] ciphertext,
            byte[] tag,
            byte[] associatedData)
        {
            if (!IsValidKey(key) || nonce == null || nonce.Length != NonceSize ||
                ciphertext == null || tag == null || tag.Length != TagSize || associatedData == null)
                return null;

            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
                return plaintext;
            }
            catch (CryptographicException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Computes a truncated, domain-separated HMAC for canonical audio-header bytes.
        /// </summary>
        public static byte[] ComputeHeaderAuthenticationTag(byte[] netKey, byte[] canonicalHeader)
        {
            ValidateKey(netKey);
            ArgumentNullException.ThrowIfNull(canonicalHeader);

            byte[] headerKey = DeriveHeaderAuthenticationKey(netKey);
            try
            {
                byte[] fullTag = HMACSHA256.HashData(headerKey, canonicalHeader);
                var result = new byte[HeaderAuthenticationTagSize];
                Buffer.BlockCopy(fullTag, 0, result, 0, result.Length);
                CryptographicOperations.ZeroMemory(fullTag);
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(headerKey);
            }
        }

        /// <summary>
        /// Verifies a canonical audio-header tag in constant time.
        /// </summary>
        public static bool VerifyHeaderAuthenticationTag(
            byte[] netKey,
            byte[] canonicalHeader,
            byte[]? authenticationTag)
        {
            if (!IsValidKey(netKey) || canonicalHeader == null ||
                authenticationTag == null || authenticationTag.Length != HeaderAuthenticationTagSize)
                return false;

            byte[] expected = ComputeHeaderAuthenticationTag(netKey, canonicalHeader);
            try
            {
                return CryptographicOperations.FixedTimeEquals(expected, authenticationTag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
            }
        }

        private static byte[] DeriveHeaderAuthenticationKey(byte[] netKey) =>
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                netKey,
                32,
                HeaderAuthenticationSalt,
                HeaderAuthenticationInfo);

        private static void ValidateKey(byte[] key)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (!IsValidKey(key))
                throw new ArgumentException("AES-GCM requires a 16, 24, or 32 byte key.", nameof(key));
        }

        private static bool IsValidKey(byte[]? key) =>
            key != null && key.Length is 16 or 24 or 32;

        private static void ValidateNonce(byte[] nonce)
        {
            ArgumentNullException.ThrowIfNull(nonce);
            if (nonce.Length != NonceSize)
                throw new ArgumentException($"RadioRelay GCM nonces must be {NonceSize} bytes.", nameof(nonce));
        }
    }
}
