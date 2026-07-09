using RadioRelay.Shared.Diagnostics;
using System;
using System.IO;

namespace RadioRelay.Server
{
    public sealed class RelayServerOptions
    {
        /// Largest UDP datagram the relay will attempt to decode.
        public int MaxDatagramBytes { get; init; } = 4096;

        /// Maximum number of datagrams accepted from a source IP in a one-second window.
        public int MaxDatagramsPerIpPerSecond { get; init; } = 200;

        /// Maximum simultaneously registered clients. Existing clients may still resubscribe.
        public int MaxClientCount { get; init; } = 256;

        /// Minimum interval between repeated malformed/auth failure log lines for the same source.
        public TimeSpan LogFloodLimitWindow { get; init; } = TimeSpan.FromSeconds(5);

        /// Path for persistent banned IP storage. Tests may inject a temp path.
        public string BanListPath { get; init; } = DefaultBanListPath;

        /// Optional best-effort file log for server lifecycle/admin/audit events.
        public LocalLog? ServerLog { get; init; }

        public static string DefaultBanListPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RadioRelay",
            "server-banlist.txt");
    }
}
