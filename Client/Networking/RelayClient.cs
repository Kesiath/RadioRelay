using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RadioRelay.Client.Diagnostics;
using RadioRelay.Shared.Diagnostics;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Client.Networking
{
    /// 
    /// UDP connection to a RadioRelay server: sends mic audio and frequency
    /// subscriptions, and raises an event for every audio packet received
    /// from other clients. Encryption/Opus are handled by the caller
    /// (AudioEngine) -- this class just moves AudioPacket objects over UDP.
    ///
    /// Also actively validates the connection is alive: UDP has no
    /// handshake, so a successful Connect()/Send() only proves a packet left
    /// this machine, not that the server ever received it or that this
    /// client could receive a reply. Every Heartbeat/Subscribe expects a
    /// HeartbeatAck back from the server; if none arrives for a while, this
    /// class raises ConnectionHealthChanged(false) so the UI can warn the
    /// user their transmissions may not actually be reaching anyone.
    /// 
    public class RelayClient : IDisposable
    {
        // A bit over 2x the 5s heartbeat interval, so a single lost/delayed
        // heartbeat round-trip doesn't cause a false "unhealthy" flap.
        private const int HealthTimeoutSeconds = 12;

        private UdpClient? _udp;
        private CancellationTokenSource? _cts;
        private System.Threading.Timer? _heartbeatTimer;
        private System.Threading.Timer? _healthCheckTimer;
        private DateTime _lastAckReceived;
        private bool _isHealthy;
        private bool _hasReportedHealthState;
        private readonly object _subscriptionSync = new();
        private PresenceSubscription[] _lastSubscriptions = Array.Empty<PresenceSubscription>();
        private readonly IClientDiagnostics? _diagnostics;

        public Guid ClientId { get; }
        public bool IsConnected { get; private set; }
        public bool IsHealthy => _isHealthy;
        public string ServerPassword { get; set; } = "";

        /// User-entered callsign, sent along with Subscribe packets
        /// so the server can log connect/disconnect and audio activity by
        /// name instead of a raw GUID. Never used for access control.
        public string Callsign { get; set; } = "";

        public event Action<AudioPacket>? AudioReceived;
        public event Action<PresenceChannelCount[]>? PresenceUpdated;
        public event Action<int>? TotalUserCountUpdated;
        public event Action<string>? StatusChanged;

        /// Fires on every healthy/unhealthy transition, including
        /// the first HeartbeatAck that proves the UDP path to the server is
        /// actually established, so the UI can show/hide warnings and play
        /// connection audio based on confirmed server reachability.
        public event Action<bool>? ConnectionHealthChanged;

        public RelayClient(Guid clientId, IClientDiagnostics? diagnostics = null)
        {
            ClientId = clientId;
            _diagnostics = diagnostics ?? ClientDiagnostics.Current;
        }

        public void Connect(string host, int port)
        {
            Connect(host, port, "");
        }

        public void Connect(string host, int port, string serverPassword)
        {
            ServerPassword = serverPassword ?? "";
            Disconnect();

            var udp = new UdpClient();
            CancellationTokenSource? cts = null;
            try
            {
                udp.Connect(host, port);
                cts = new CancellationTokenSource();
                _udp = udp;
                _cts = cts;
                IsConnected = true;
                _isHealthy = false;
                _hasReportedHealthState = false;
                _lastAckReceived = DateTime.MinValue; // not established until a server ack proves the UDP path is working

                _ = Task.Run(() => ReceiveLoop(cts.Token));
                _heartbeatTimer = new System.Threading.Timer(_ => SendHeartbeat(), null, 0, 5000);
                _healthCheckTimer = new System.Threading.Timer(_ => CheckHealth(), null, 2000, 2000);

                RaiseStatusChanged($"Connecting to {host}:{port}");
            }
            catch (Exception ex)
            {
                _diagnostics?.LogException(ErrorCodes.ClientConnectFailure, $"connect host={host} port={port}", ex);
                cts?.Dispose();
                udp.Dispose();
                _udp = null;
                _cts = null;
                IsConnected = false;
                _isHealthy = false;
                _hasReportedHealthState = false;
                throw;
            }
        }

        public void Disconnect()
        {
            if (!IsConnected) return;
            IsConnected = false;
            _heartbeatTimer?.Dispose();
            _healthCheckTimer?.Dispose();

            try
            {
                var data = new HeartbeatPacket { ClientId = ClientId, ServerPassword = ServerPassword }.Encode(PacketType.Disconnect);
                _udp?.Send(data, data.Length);
            }
            catch { /* best-effort -- the server will time us out anyway */ }

            _cts?.Cancel();
            _udp?.Close();
            _isHealthy = false;
            _hasReportedHealthState = false;
            RaiseStatusChanged("Disconnected");
        }

        public void SendSubscribe(float[] frequencies)
        {
            SendSubscribe(Array.ConvertAll(frequencies,
                f => new PresenceSubscription { Frequency = f, NetIdHash = new byte[8] }));
        }

        public void SendSubscribe(PresenceSubscription[] subscriptions)
        {
            var snapshot = CloneSubscriptions(subscriptions);
            lock (_subscriptionSync)
            {
                _lastSubscriptions = snapshot;
            }

            SendSubscribeSnapshot(snapshot);
        }

        private void SendSubscribeSnapshot(PresenceSubscription[] subscriptions)
        {
            if (_udp == null || !IsConnected) return;
            try
            {
                var data = new SubscribePacket
                {
                    ClientId = ClientId,
                    Frequencies = Array.ConvertAll(subscriptions, s => s.Frequency),
                    Subscriptions = subscriptions,
                    Callsign = Callsign,
                    ServerPassword = ServerPassword
                }.Encode();
                _udp.Send(data, data.Length);
            }
            catch (Exception ex)
            {
                RaiseStatusChanged($"Failed to send subscribe: {ex.Message}");
            }
        }

        private PresenceSubscription[] GetLastSubscriptionsSnapshot()
        {
            lock (_subscriptionSync)
            {
                return CloneSubscriptions(_lastSubscriptions);
            }
        }

        private static PresenceSubscription[] CloneSubscriptions(PresenceSubscription[] subscriptions)
        {
            if (subscriptions == null || subscriptions.Length == 0) return Array.Empty<PresenceSubscription>();

            var clone = new PresenceSubscription[subscriptions.Length];
            for (int i = 0; i < subscriptions.Length; i++)
            {
                clone[i] = new PresenceSubscription
                {
                    Frequency = subscriptions[i].Frequency,
                    NetIdHash = CloneNetIdHash(subscriptions[i].NetIdHash)
                };
            }

            return clone;
        }

        private static byte[] CloneNetIdHash(byte[]? hash)
        {
            var clone = new byte[8];
            if (hash != null)
                Array.Copy(hash, clone, Math.Min(clone.Length, hash.Length));
            return clone;
        }

        /// ClientId is stamped on here so callers don't need to set it themselves.
        public void SendAudio(AudioPacket packet)
        {
            if (_udp == null || !IsConnected) return;
            packet.ClientId = ClientId;
            try
            {
                var data = packet.Encode();
                _udp.Send(data, data.Length);
            }
            catch (Exception ex)
            {
                // A send failing outright (e.g. "network unreachable") is a
                // strong, immediate signal transmissions aren't going
                // through -- don't wait for the next health-check tick.
                RaiseStatusChanged($"Failed to send audio: {ex.Message}");
                _diagnostics?.LogException(ErrorCodes.ClientSendAudioFailure, $"send audio frequency={packet.Frequency:0.000}", ex);
                SetHealthy(false);
            }
        }

        private void SendHeartbeat()
        {
            if (_udp == null || !IsConnected) return;

            // A Subscribe packet is a stronger heartbeat: it refreshes LastSeen,
            // carries the server password, receives the same HeartbeatAck, and
            // recreates this client in the server's user table after a server
            // restart. Without this, a restarted server can ACK heartbeats for
            // an unknown client while still dropping that client's audio because
            // no ClientState exists for its ClientId.
            var subscriptions = GetLastSubscriptionsSnapshot();
            if (subscriptions.Length > 0)
            {
                SendSubscribeSnapshot(subscriptions);
                return;
            }

            try
            {
                var data = new HeartbeatPacket { ClientId = ClientId, ServerPassword = ServerPassword }.Encode();
                _udp.Send(data, data.Length);
            }
            catch { /* socket may be closing; the health check will catch a real problem */ }
        }

        private void CheckHealth()
        {
            if (!IsConnected) return;
            bool healthy = (DateTime.UtcNow - _lastAckReceived).TotalSeconds <= HealthTimeoutSeconds;
            SetHealthy(healthy);
        }

        private void SetHealthy(bool healthy)
        {
            if (_hasReportedHealthState && healthy == _isHealthy) return;
            _isHealthy = healthy;
            _hasReportedHealthState = true;
            SafeInvoke(ConnectionHealthChanged, healthy);
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _udp != null)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; }

                if (result.Buffer.Length == 0) continue;

                try
                {
                    HandleIncomingPacket(result.Buffer);
                }
                catch (Exception ex) when (IsMalformedPacketException(ex))
                {
                    RaiseStatusChanged($"Dropped malformed packet from server: {ex.Message}");
                    _diagnostics?.LogException(ErrorCodes.ClientMalformedServerPacket, "receive loop dropped malformed server packet", ex);
                }
            }
        }

        private void HandleIncomingPacket(byte[] data)
        {
            var type = PacketPeek.GetType(data);

            if (type == PacketType.HeartbeatAck)
            {
                var ack = HeartbeatPacket.Decode(data);
                if (ack.ClientId != ClientId) return;

                _lastAckReceived = DateTime.UtcNow;
                SetHealthy(true);
                return;
            }

            if (type == PacketType.PresenceUpdate)
            {
                var presence = PresenceUpdatePacket.Decode(data);
                SafeInvoke(PresenceUpdated, presence.Counts);
                SafeInvoke(TotalUserCountUpdated, presence.TotalUserCount);
                return;
            }

            if (type != PacketType.Audio) return;

            var packet = AudioPacket.Decode(data);
            if (packet.ClientId == ClientId) return; // server already excludes sender, extra safety
            SafeInvoke(AudioReceived, packet);
        }

        private static bool IsMalformedPacketException(Exception ex) =>
            ex is EndOfStreamException or IOException or ArgumentException;

        private void RaiseStatusChanged(string message) => SafeInvoke(StatusChanged, message);

        private static void SafeInvoke<T>(Action<T>? handlers, T arg)
        {
            if (handlers == null) return;

            foreach (Action<T> handler in handlers.GetInvocationList().Cast<Action<T>>())
            {
                try { handler(arg); }
                catch { /* UI/background subscriber failures must not stop network loops or timers. */ }
            }
        }

        public void Dispose() => Disconnect();
    }
}
