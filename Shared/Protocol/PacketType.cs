namespace RadioRelay.Shared.Protocol
{
    /// <summary>
    /// First byte of every UDP datagram exchanged with the relay server.
    /// </summary>
    public enum PacketType : byte
    {
        Audio = 1,
        Subscribe = 2,
        Heartbeat = 3,
        Disconnect = 4,

        /// <summary>
        /// Acknowledges subscriptions and heartbeats for bidirectional reachability checks.
        /// </summary>
        HeartbeatAck = 5,

        /// <summary>
        /// Broadcasts membership grouped by frequency and net hash.
        /// </summary>
        PresenceUpdate = 6
    }
}
