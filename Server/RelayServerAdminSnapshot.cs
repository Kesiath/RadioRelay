using System;
using System.Net;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Server
{
    public sealed record RelayServerClientSnapshot(
        Guid ClientId,
        string Callsign,
        IPEndPoint EndPoint,
        float[] Frequencies,
        PresenceSubscription[] Subscriptions,
        DateTime LastSeenUtc);

    public sealed record RelayServerKickResult(
        bool Success,
        string Selector,
        RelayServerClientSnapshot? Client,
        string Reason);

    public sealed record RelayServerStatsSnapshot(
        int ConnectedClients,
        int BannedIpCount,
        TimeSpan Uptime,
        long DatagramsReceived,
        long DatagramsRelayed,
        long DatagramsDropped);
}
