using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RadioRelay.Shared.Diagnostics;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Server
{
    public class ClientState
    {
        public IPEndPoint EndPoint = null!;
        public float[] Frequencies = Array.Empty<float>();
        public PresenceSubscription[] Subscriptions = Array.Empty<PresenceSubscription>();
        public string Callsign = "";
        public DateTime LastSeen;
    }

    /// 
    /// Frequency-based UDP relay. Clients tell the server which frequencies
    /// they're listening on (Subscribe packets); when an Audio packet arrives
    /// on frequency F, it is forwarded to every other client currently
    /// listening on a frequency within tolerance of F. There is no concept
    /// of a fixed "channel" -- grouping is purely by frequency match.
    ///
    /// The server logs connection lifecycle, auth/admin, and malformed packet
    /// events to the console. Per-transmission voice activity is intentionally
    /// not logged so live conversations do not clutter the server output.
    /// 
    public class RelayServer
    {
        // 0.005 MHz = 5 kHz tolerance, similar spacing to an analog radio dial.
        private const float FrequencyTolerance = 0.005f;
        private const int ClientTimeoutSeconds = 20;

        private readonly UdpClient _udp;
        private readonly ConcurrentDictionary<Guid, ClientState> _clients = new();
        private readonly ConcurrentDictionary<IPAddress, byte> _bannedAddresses = new();
        private readonly ConcurrentDictionary<IPAddress, RateLimitState> _rateLimits = new();
        private readonly ConcurrentDictionary<string, FloodLogState> _floodLogs = new();
        private readonly string _serverPassword;
        private readonly RelayServerOptions _options;
        private readonly PersistentBanList _persistentBanList;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private long _datagramsReceived;
        private long _datagramsRelayed;
        private long _datagramsDropped;

        public int ConnectedClients => _clients.Count;
        public IReadOnlyCollection<IPAddress> BannedAddresses => _bannedAddresses.Keys.ToArray();

        public RelayServer(int port)
            : this(port, "")
        {
        }

        public RelayServer(int port, string serverPassword)
            : this(port, serverPassword, new RelayServerOptions())
        {
        }

        public RelayServer(int port, string serverPassword, RelayServerOptions options)
        {
            _udp = new UdpClient(port);
            _serverPassword = serverPassword ?? "";
            _options = options ?? new RelayServerOptions();
            _persistentBanList = new PersistentBanList(_options.BanListPath);
            foreach (var address in LoadPersistedBans())
                _bannedAddresses.TryAdd(NormalizeAddress(address), 0);
        }

        public async Task RunAsync(CancellationToken ct)
        {
            _ = Task.Run(() => CleanupLoop(ct), ct);
            Console.WriteLine($"[RelayServer] Listening on UDP port {((IPEndPoint)_udp.Client.LocalEndPoint!).Port}");

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync(ct);
                    Interlocked.Increment(ref _datagramsReceived);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (result.Buffer.Length > _options.MaxDatagramBytes)
                {
                    WriteFloodLimited(
                        $"oversized:{NormalizeAddress(result.RemoteEndPoint.Address)}",
                        $"[Warning] Dropped oversized datagram from {result.RemoteEndPoint}: {result.Buffer.Length} bytes exceeds {_options.MaxDatagramBytes}");
                    Interlocked.Increment(ref _datagramsDropped);
                    continue;
                }

                if (IsRateLimited(result.RemoteEndPoint.Address))
                {
                    WriteFloodLimited(
                        $"rate:{NormalizeAddress(result.RemoteEndPoint.Address)}",
                        $"[Warning] Rate limited UDP datagrams from {result.RemoteEndPoint.Address}");
                    Interlocked.Increment(ref _datagramsDropped);
                    continue;
                }

                try
                {
                    HandlePacket(result.Buffer, result.RemoteEndPoint);
                }
                catch (Exception ex) when (IsMalformedPacketException(ex))
                {
                    WriteFloodLimited(
                        $"malformed:{NormalizeAddress(result.RemoteEndPoint.Address)}",
                        $"[Warning] Dropped malformed packet from {result.RemoteEndPoint}: {ex.Message}");
                    Interlocked.Increment(ref _datagramsDropped);
                }
            }
        }

        private static bool IsMalformedPacketException(Exception ex) =>
            ex is EndOfStreamException or IOException or ArgumentException;

        private void HandlePacket(byte[] data, IPEndPoint from)
        {
            if (data.Length == 0) return;
            if (IsBanned(from.Address))
            {
                WriteFloodLimited(
                    $"ban:{NormalizeAddress(from.Address)}",
                    $"[Ban] Rejected packet from {from}: action=reject result=blocked reason=banned-ip");
                Interlocked.Increment(ref _datagramsDropped);
                return;
            }

            switch (PacketPeek.GetType(data))
            {
                case PacketType.Subscribe:
                    {
                        var p = SubscribePacket.Decode(data);
                        if (!IsAuthorized(p.ServerPassword))
                        {
                            WriteFloodLimited(
                                $"auth:{NormalizeAddress(from.Address)}",
                                $"[Auth] Rejected subscribe from {from}: action=subscribe result=rejected reason=invalid-server-password");
                            Interlocked.Increment(ref _datagramsDropped);
                            break;
                        }

                        bool isNewClient = !_clients.ContainsKey(p.ClientId);
                        if (isNewClient && _clients.Count >= _options.MaxClientCount)
                        {
                            WriteFloodLimited(
                                $"max-clients:{NormalizeAddress(from.Address)}",
                                $"[Warning] Rejected subscribe from {from}: server client limit {_options.MaxClientCount} reached");
                            Interlocked.Increment(ref _datagramsDropped);
                            break;
                        }

                        _clients.AddOrUpdate(p.ClientId,
                            _ => new ClientState
                            {
                                EndPoint = from,
                                Frequencies = p.Frequencies,
                                Subscriptions = p.Subscriptions,
                                Callsign = p.Callsign,
                                LastSeen = DateTime.UtcNow
                            },
                            (_, existing) =>
                            {
                                existing.EndPoint = from;
                                existing.Frequencies = p.Frequencies;
                                existing.Subscriptions = p.Subscriptions;
                                existing.Callsign = p.Callsign;
                                existing.LastSeen = DateTime.UtcNow;
                                return existing;
                            });

                        if (isNewClient)
                        {
                            var name = string.IsNullOrWhiteSpace(p.Callsign) ? "(no callsign)" : p.Callsign;
                            Console.WriteLine($"[Connect] {name} connected from {from}");
                        }

                        SendAck(p.ClientId, from);
                        BroadcastPresenceUpdate();
                        break;
                    }
                case PacketType.Heartbeat:
                    {
                        var p = HeartbeatPacket.Decode(data);
                        if (_clients.TryGetValue(p.ClientId, out var state) && state.EndPoint.Equals(from))
                        {
                            state.LastSeen = DateTime.UtcNow;
                            SendAck(p.ClientId, from);
                        }
                        else if (IsAuthorized(p.ServerPassword))
                        {
                            SendAck(p.ClientId, from);
                        }
                        break;
                    }
                case PacketType.Audio:
                    {
                        var p = AudioPacket.Decode(data);
                        if (!_clients.TryGetValue(p.ClientId, out var sender))
                        {
                            Interlocked.Increment(ref _datagramsDropped);
                            break;
                        }

                        if (!sender.EndPoint.Equals(from))
                        {
                            Interlocked.Increment(ref _datagramsDropped);
                            break;
                        }

                        sender.LastSeen = DateTime.UtcNow;
                        sender.EndPoint = from;

                        RelayAudio(p, data, from);
                        break;
                    }
                case PacketType.Disconnect:
                    {
                        var p = HeartbeatPacket.Decode(data);
                        if (_clients.TryGetValue(p.ClientId, out var existing) &&
                            existing.EndPoint.Equals(from) &&
                            _clients.TryRemove(p.ClientId, out var removed))
                        {
                            var name = string.IsNullOrWhiteSpace(removed.Callsign) ? "(no callsign)" : removed.Callsign;
                            Console.WriteLine($"[Disconnect] {name} disconnected");
                            BroadcastPresenceUpdate();
                        }
                        break;
                    }
            }
        }

        public bool BanAddress(IPAddress address)
        {
            var normalized = NormalizeAddress(address);
            bool added = _bannedAddresses.TryAdd(normalized, 0);
            bool removedClient = false;

            foreach (var kvp in _clients)
            {
                if (NormalizeAddress(kvp.Value.EndPoint.Address).Equals(normalized) && _clients.TryRemove(kvp.Key, out var removed))
                {
                    removedClient = true;
                    var name = string.IsNullOrWhiteSpace(removed.Callsign) ? "(no callsign)" : removed.Callsign;
                    Console.WriteLine($"[Ban] Disconnected banned client {name} from {removed.EndPoint}");
                }
            }

            if (removedClient) BroadcastPresenceUpdate();
            if (added) SavePersistedBans();
            return added;
        }

        public bool UnbanAddress(IPAddress address)
        {
            bool removed = _bannedAddresses.TryRemove(NormalizeAddress(address), out _);
            if (removed) SavePersistedBans();
            return removed;
        }

        public bool IsBanned(IPAddress address) =>
            _bannedAddresses.ContainsKey(NormalizeAddress(address));

        public RelayServerClientSnapshot[] GetClientSnapshots()
        {
            return _clients
                .Select(kvp => CreateClientSnapshot(kvp.Key, kvp.Value))
                .OrderBy(snapshot => snapshot.Callsign)
                .ThenBy(snapshot => snapshot.ClientId)
                .ToArray();
        }

        public RelayServerKickResult KickClient(string selector, string reason)
        {
            selector = selector?.Trim() ?? "";
            if (selector.Length == 0)
                return new RelayServerKickResult(false, selector, null, "missing-selector");

            var matches = _clients
                .Where(kvp => ClientMatchesSelector(kvp.Key, kvp.Value, selector))
                .ToArray();

            if (matches.Length == 0)
                return new RelayServerKickResult(false, selector, null, "not-found");

            if (matches.Length > 1)
                return new RelayServerKickResult(false, selector, null, "ambiguous");

            var match = matches[0];
            if (!_clients.TryRemove(match.Key, out var removed))
                return new RelayServerKickResult(false, selector, null, "not-found");

            var snapshot = CreateClientSnapshot(match.Key, removed);
            BroadcastPresenceUpdate();
            Console.WriteLine($"[Admin] Kicked {DisplayName(snapshot.Callsign)} ({snapshot.ClientId}) from {snapshot.EndPoint}: reason={reason}");
            return new RelayServerKickResult(true, selector, snapshot, reason);
        }

        public RelayServerStatsSnapshot GetStatsSnapshot() => new(
            ConnectedClients,
            BannedAddresses.Count,
            DateTime.UtcNow - _startedUtc,
            Interlocked.Read(ref _datagramsReceived),
            Interlocked.Read(ref _datagramsRelayed),
            Interlocked.Read(ref _datagramsDropped));

        private static RelayServerClientSnapshot CreateClientSnapshot(Guid clientId, ClientState state) => new(
            clientId,
            state.Callsign,
            state.EndPoint,
            state.Frequencies.ToArray(),
            state.Subscriptions.ToArray(),
            state.LastSeen);

        private static bool ClientMatchesSelector(Guid clientId, ClientState state, string selector)
        {
            if (clientId.ToString("D").StartsWith(selector, StringComparison.OrdinalIgnoreCase)) return true;
            if (clientId.ToString("N").StartsWith(selector, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(state.Callsign, selector, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(NormalizeAddress(state.EndPoint.Address).ToString(), selector, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string DisplayName(string callsign) =>
            string.IsNullOrWhiteSpace(callsign) ? "(no callsign)" : callsign;

        private IReadOnlyCollection<IPAddress> LoadPersistedBans()
        {
            try
            {
                return _persistentBanList.Load();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Console.WriteLine($"[Warning] Failed to load banlist: {ex.Message}");
                return Array.Empty<IPAddress>();
            }
        }

        private void SavePersistedBans()
        {
            try
            {
                _persistentBanList.Save(_bannedAddresses.Keys);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Console.WriteLine($"[Warning] Failed to save banlist: {ex.Message}");
            }
        }

        private bool IsAuthorized(string password) =>
            string.IsNullOrEmpty(_serverPassword) || string.Equals(_serverPassword, password ?? "", StringComparison.Ordinal);

        private static IPAddress NormalizeAddress(IPAddress address) =>
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        private bool IsRateLimited(IPAddress address)
        {
            if (_options.MaxDatagramsPerIpPerSecond <= 0) return false;

            var normalized = NormalizeAddress(address);
            var state = _rateLimits.GetOrAdd(normalized, _ => new RateLimitState());
            return !state.TryAcquire(_options.MaxDatagramsPerIpPerSecond, DateTime.UtcNow);
        }

        private void WriteFloodLimited(string key, string message)
        {
            var window = _options.LogFloodLimitWindow;
            if (window <= TimeSpan.Zero)
            {
                Console.WriteLine(message);
                _options.ServerLog?.LogLifecycle(ErrorCodes.ServerAdminAudit, message);
                return;
            }

            var state = _floodLogs.GetOrAdd(key, _ => new FloodLogState());
            if (state.ShouldLog(window, DateTime.UtcNow))
            {
                Console.WriteLine(message);
                _options.ServerLog?.LogLifecycle(ErrorCodes.ServerAdminAudit, message);
            }
        }

        private sealed class RateLimitState
        {
            private readonly object _sync = new();
            private DateTime _windowStartedUtc = DateTime.MinValue;
            private int _count;

            public bool TryAcquire(int maxPerSecond, DateTime nowUtc)
            {
                lock (_sync)
                {
                    if (nowUtc - _windowStartedUtc >= TimeSpan.FromSeconds(1))
                    {
                        _windowStartedUtc = nowUtc;
                        _count = 0;
                    }

                    if (_count >= maxPerSecond) return false;
                    _count++;
                    return true;
                }
            }
        }

        private sealed class FloodLogState
        {
            private readonly object _sync = new();
            private DateTime _lastLoggedUtc = DateTime.MinValue;

            public bool ShouldLog(TimeSpan window, DateTime nowUtc)
            {
                lock (_sync)
                {
                    if (nowUtc - _lastLoggedUtc < window) return false;
                    _lastLoggedUtc = nowUtc;
                    return true;
                }
            }
        }

        /// Confirms receipt to the client so it can actually
        /// validate the connection is alive end-to-end, rather than just
        /// assuming it's fine because sending never throws (UDP send
        /// "succeeding" only means the packet left this machine, not that
        /// anyone received it).
        private void SendAck(Guid clientId, IPEndPoint to)
        {
            try
            {
                var ack = new HeartbeatPacket { ClientId = clientId }.Encode(PacketType.HeartbeatAck);
                SendDatagram(ack, to);
            }
            catch
            {
                // Best-effort -- if this particular send fails, the client's
                // own health-check timeout will surface the problem anyway.
            }
        }

        private void BroadcastPresenceUpdate()
        {
            var subscriptions = _clients.Values.SelectMany(client =>
                client.Subscriptions.Length > 0
                    ? client.Subscriptions
                    : Array.ConvertAll(client.Frequencies,
                        f => new PresenceSubscription { Frequency = f, NetIdHash = new byte[8] }));
            var data = new PresenceUpdatePacket { Counts = PresenceCounter.Build(subscriptions) }.Encode();

            foreach (var client in _clients.Values)
            {
                try { SendDatagram(data, client.EndPoint); }
                catch { /* Best-effort; clients will receive the next update. */ }
            }
        }

        private void RelayAudio(AudioPacket packet, byte[] raw, IPEndPoint senderEndPoint)
        {
            foreach (var kvp in _clients)
            {
                var id = kvp.Key;
                var state = kvp.Value;
                if (id == packet.ClientId) continue;
                if (state.EndPoint.Equals(senderEndPoint)) continue;

                if (!ClientIsSubscribedToAudio(state, packet)) continue;

                try
                {
                    SendDatagram(raw, state.EndPoint);
                    Interlocked.Increment(ref _datagramsRelayed);
                }
                catch (Exception ex) when (IsSendFailureException(ex))
                {
                    Console.WriteLine($"[Warning] Failed to relay audio to {state.EndPoint}: {ex.Message}");
                }
            }
        }

        private static bool ClientIsSubscribedToAudio(ClientState state, AudioPacket packet)
        {
            if (state.Subscriptions.Length > 0)
            {
                var requiredHash = packet.IsEncrypted ? packet.NetIdHash : new byte[8];
                return state.Subscriptions.Any(subscription => subscription.Matches(packet.Frequency, requiredHash));
            }

            if (packet.IsEncrypted) return false;

            return state.Frequencies.Any(f => Math.Abs(f - packet.Frequency) <= FrequencyTolerance);
        }

        protected virtual void SendDatagram(byte[] data, IPEndPoint to) =>
            _udp.Send(data, data.Length, to);

        private static bool IsSendFailureException(Exception ex) =>
            ex is SocketException or ObjectDisposedException or InvalidOperationException;

        private async Task CleanupLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }

                var cutoff = DateTime.UtcNow.AddSeconds(-ClientTimeoutSeconds);
                foreach (var kvp in _clients)
                {
                    if (kvp.Value.LastSeen < cutoff && _clients.TryRemove(kvp.Key, out var removed))
                    {
                        var name = string.IsNullOrWhiteSpace(removed.Callsign) ? "(no callsign)" : removed.Callsign;
                        Console.WriteLine($"[Disconnect] {name} timed out");
                        BroadcastPresenceUpdate();
                    }
                }
            }
        }
    }
}
