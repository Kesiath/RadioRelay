namespace RadioRelay.Shared.Protocol
{
    /// 
    /// First byte of every UDP datagram exchanged with the relay server.
    /// 
    public enum PacketType : byte
    {
        Audio = 1,
        Subscribe = 2,
        Heartbeat = 3,
        Disconnect = 4,

        /// Sent by the server back to a client immediately upon
        /// receiving a Subscribe or Heartbeat from it. Lets the client
        /// actually verify the server is alive and reachable, rather than
        /// just assuming a UDP "connect" succeeded (UDP has no handshake,
        /// so without this a client has no way to distinguish "connected
        /// and working" from "sending into a void").
        HeartbeatAck = 5,

        /// Broadcast by the server whenever subscription presence
        /// changes. Each entry is grouped by frequency plus net/key hash so
        /// keyed radios only show users on the same key.
        PresenceUpdate = 6
    }
}
