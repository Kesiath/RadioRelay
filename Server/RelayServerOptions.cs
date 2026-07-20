using RadioRelay.Shared.Diagnostics;
using System;
using System.IO;

namespace RadioRelay.Server
{
    public sealed class RelayServerOptions
    {
        /// <summary>
        /// Largest UDP datagram the relay will attempt to decode.
        /// </summary>
        public int MaxDatagramBytes { get; init; } = 4096;

        /// <summary>
        /// Sustained audio datagrams allowed per registered client each second.
        /// </summary>
        public int MaxAudioDatagramsPerClientPerSecond { get; init; } = 200;

        /// <summary>
        /// Maximum short audio burst accepted from one registered client.
        /// </summary>
        public int AudioDatagramBurstPerClient { get; init; } = 100;

        /// <summary>
        /// Sustained control datagrams accepted from one registered client.
        /// </summary>
        public int MaxControlDatagramsPerClientPerSecond { get; init; } = 30;

        /// <summary>
        /// Maximum short control burst accepted from one registered client.
        /// </summary>
        public int ControlDatagramBurstPerClient { get; init; } = 60;

        /// <summary>
        /// Sustained unregistered traffic allowed per UDP endpoint each second.
        /// </summary>
        public int MaxUnregisteredDatagramsPerEndpointPerSecond { get; init; } = 30;

        /// <summary>
        /// Maximum short pre-registration burst from one UDP endpoint.
        /// </summary>
        public int UnregisteredDatagramBurstPerEndpoint { get; init; } = 60;

        /// <summary>
        /// Aggregate unregistered traffic allowed per public IP each second.
        /// </summary>
        public int MaxUnregisteredDatagramsPerIpPerSecond { get; init; } = 500;

        /// <summary>
        /// Maximum short aggregate pre-registration burst from a public IP.
        /// </summary>
        public int UnregisteredDatagramBurstPerIp { get; init; } = 1000;

        /// <summary>
        /// Maximum simultaneously registered clients. Existing clients may still resubscribe.
        /// </summary>
        public int MaxClientCount { get; init; } = 256;

        /// <summary>
        /// Minimum interval between repeated malformed/auth failure log lines for the same source.
        /// </summary>
        public TimeSpan LogFloodLimitWindow { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Path for persistent banned IP storage. Tests may inject a temp path.
        /// </summary>
        public string BanListPath { get; init; } = DefaultBanListPath;

        /// <summary>
        /// Optional best-effort file log for server lifecycle/admin/audit events.
        /// </summary>
        public LocalLog? ServerLog { get; init; }

        public static string DefaultBanListPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RadioRelay",
            "server-banlist.txt");
    }
}
