using RadioRelay.Shared.Security;

namespace RadioRelay.Client.Radio
{
    /// 
    /// A radio's current encryption state, derived live from whatever
    /// passcode (if any) is typed into that radio's passcode field: either
    /// Unencrypted (anyone tuned to the frequency can hear you) or keyed off
    /// a shared passcode (only other radios with the same passcode typed
    /// into them -- anywhere, no login required -- can decrypt your audio).
    /// 
    public class NetOption
    {
        public static readonly NetOption Unencrypted = new()
        {
            DisplayName = "Unencrypted (open)",
            Key = null,
            NetIdHash = new byte[8]
        };

        public string DisplayName { get; init; } = "";
        public byte[]? Key { get; init; }
        public byte[] NetIdHash { get; init; } = new byte[8];

        public bool IsUnencrypted => Key == null;

        /// Builds the live NetOption for whatever passcode is
        /// currently typed into a radio's passcode field. Blank/whitespace
        /// means unencrypted.
        public static NetOption FromPasscode(string? passcode)
        {
            if (string.IsNullOrWhiteSpace(passcode)) return Unencrypted;

            return new NetOption
            {
                DisplayName = "Passcode-protected",
                Key = NetKeyDerivation.DeriveNetKey(passcode),
                NetIdHash = NetKeyDerivation.ComputeNetIdHash(passcode)
            };
        }

        public override string ToString() => DisplayName;
    }
}
