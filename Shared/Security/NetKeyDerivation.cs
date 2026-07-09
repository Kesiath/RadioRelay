using System.Security.Cryptography;
using System.Text;

namespace RadioRelay.Shared.Security
{
    /// 
    /// Turns a user-entered passcode into (a) a symmetric AES key shared by
    /// everyone who types the same passcode into a radio, and (b) a public
    /// routing tag so receivers can tell which of their own known passcode
    /// keys to try, without the passcode ever being sent on the wire.
    ///
    /// Security model, stated plainly: this is "soft" access control
    /// appropriate for keeping a group's radio traffic private from randoms
    /// squatting on the same frequency -- it is NOT resistant to a brute
    /// force / dictionary attack against a weak passcode, since the whole
    /// point is that the passcode itself (not some app-wide secret) is what
    /// two radios need to share in order to hear each other. Pick a passcode
    /// like you would a shared Wi-Fi password: long enough that guessing it
    /// isn't practical for your threat model.
    /// 
    public static class NetKeyDerivation
    {
        // Fixed, non-secret domain-separation strings -- these do not need
        // to be kept secret (unlike the old per-app "AppSharedSecret"): the
        // passcode itself is the only thing that has to match between two
        // radios, so there is nothing else here for a user to configure.
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
