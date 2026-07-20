using System.Security.Cryptography;
using System.Text;

namespace RadioRelay.Shared.Security
{
    /// <summary>
    /// Derives an AES key and public routing hash from a shared passcode.
    /// Weak passcodes remain vulnerable to offline guessing.
    /// </summary>
    public static class NetKeyDerivation
    {
        // Fixed domain-separation values keep key derivation purposes distinct.
        private const string Salt = "RadioRelay-Passcode-Salt-v1";
        private const string Info = "RadioRelay-NetKey-v1";
        private const string NetIdPrefix = "RadioRelay-NetId-Passcode-";

        public static byte[] DeriveNetKey(string passcode)
        {
            var ikm = Encoding.UTF8.GetBytes(passcode);
            var salt = Encoding.UTF8.GetBytes(Salt);
            var info = Encoding.UTF8.GetBytes(Info);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, info);
        }

        public static byte[] ComputeNetIdHash(string passcode)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(NetIdPrefix + passcode));
            var result = new byte[8];
            System.Array.Copy(hash, result, 8);
            return result;
        }
    }
}
