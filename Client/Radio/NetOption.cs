using RadioRelay.Shared.Security;

namespace RadioRelay.Client.Radio
{
    /// <summary>
    /// Represents an open or passcode-derived encrypted radio net.
    /// </summary>
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

        /// <summary>
        /// Builds a net from a passcode; blank input selects the open net.
        /// </summary>
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
