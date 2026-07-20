using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>
    /// Relays UDP audio by frequency and net subscription while enforcing
    /// authentication, lifecycle, and rate limits.
    /// </summary>
    public class RelayServer
    {
        // Match frequencies within 5 kHz.
        private const float FrequencyTolerance = 0.005f;
        private const int ClientTimeoutSeconds = 20;
        private static readonly byte[] ZeroNetIdHash = new byte[8];

        private readonly UdpClient _udp;
        private readonly ConcurrentDictionary<Guid, ClientState> _clients = new();
        private readonly ConcurrentDictionary<IPAddress, byte> _bannedAddresses = new();
        private readonly ConcurrentDictionary<Guid, RateLimitState> _audioRateLimits = new();
        private readonly ConcurrentDictionary<Guid, RateLimitState> _controlRateLimits = new();
        private readonly ConcurrentDictionary<Guid, ClientTransmissionTracker> _transmissionTrackers = new();
        private readonly ConcurrentDictionary<SourceEndpointKey, RateLimitState> _unregisteredEndpointRateLimits = new();
        private readonly ConcurrentDictionary<IPAddress, RateLimitState> _unregisteredIpRateLimits = new();
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

                if (IsRateLimited(result.Buffer, result.RemoteEndPoint, out var rateLimitScope))
                {
                    WriteFloodLimited(
                        $"rate:{rateLimitScope}",
                        $"[Warning] Rate limited UDP datagrams from {result.RemoteEndPoint} ({rateLimitScope})");
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

                        bool isNewClient = false;
                        if (_clients.TryGetValue(p.ClientId, out var clientState) &&
                            !clientState.EndPoint.Equals(from))
                        {
                            WriteFloodLimited(
                                $"identity:{p.ClientId}",
                                $"[Warning] Rejected subscribe from {from}: action=subscribe result=rejected reason=client-id-owned-by-active-endpoint");
                            Interlocked.Increment(ref _datagramsDropped);
                            break;
                        }

                        if (clientState == null && _clients.Count >= _options.MaxClientCount)
                        {
                            WriteFloodLimited(
                                $"max-clients:{NormalizeAddress(from.Address)}",
                                $"[Warning] Rejected subscribe from {from}: server client limit {_options.MaxClientCount} reached");
                            Interlocked.Increment(ref _datagramsDropped);
                            break;
                        }

                        if (clientState == null)
                        {
                            var candidate = new ClientState
                            {
                                EndPoint = from,
                                Frequencies = p.Frequencies,
                                Subscriptions = p.Subscriptions,
                                Callsign = p.Callsign,
                                LastSeen = DateTime.UtcNow
                            };
                            if (_clients.TryAdd(p.ClientId, candidate))
                            {
                                clientState = candidate;
                                isNewClient = true;
                            }
                            else if (!_clients.TryGetValue(p.ClientId, out clientState) ||
                                     !clientState.EndPoint.Equals(from))
                            {
                                Interlocked.Increment(ref _datagramsDropped);
                                break;
                            }
                        }

                        clientState.EndPoint = from;
                        clientState.Frequencies = p.Frequencies;
                        clientState.Subscriptions = p.Subscriptions;
                        clientState.Callsign = p.Callsign;
                        clientState.LastSeen = DateTime.UtcNow;

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
                            // Heartbeats probe reachability; subscriptions maintain presence.
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

                        if (!ValidateAndTrackTransmission(sender, p))
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
                            RemoveClientRateLimits(p.ClientId);
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
                    RemoveClientRateLimits(kvp.Key);
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

            RemoveClientRateLimits(match.Key);
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

        private bool IsRateLimited(byte[] data, IPEndPoint source, out string scope)
        {
            long now = Stopwatch.GetTimestamp();
            if (TryGetRegisteredClient(data, source, out var clientId))
            {
                bool isAudio = data.Length > 0 && PacketPeek.GetType(data) == PacketType.Audio;
                var limits = isAudio ? _audioRateLimits : _controlRateLimits;
                int rate = isAudio
                    ? _options.MaxAudioDatagramsPerClientPerSecond
                    : _options.MaxControlDatagramsPerClientPerSecond;
                int burst = isAudio
                    ? _options.AudioDatagramBurstPerClient
                    : _options.ControlDatagramBurstPerClient;
                var state = limits.GetOrAdd(clientId, _ => new RateLimitState());
                if (state.TryAcquire(rate, burst, now))
                {
                    scope = "";
                    return false;
                }
                scope = $"client:{clientId:N}:{(isAudio ? "audio" : "control")}";
                return true;
            }

            var normalizedAddress = NormalizeAddress(source.Address);
            var endpointKey = new SourceEndpointKey(normalizedAddress, source.Port);
            var endpointState = _unregisteredEndpointRateLimits.GetOrAdd(endpointKey, _ => new RateLimitState());
            if (!endpointState.TryAcquire(
                    _options.MaxUnregisteredDatagramsPerEndpointPerSecond,
                    _options.UnregisteredDatagramBurstPerEndpoint,
                    now))
            {
                scope = $"endpoint:{normalizedAddress}:{source.Port}:unregistered";
                return true;
            }

            var ipState = _unregisteredIpRateLimits.GetOrAdd(normalizedAddress, _ => new RateLimitState());
            if (ipState.TryAcquire(
                    _options.MaxUnregisteredDatagramsPerIpPerSecond,
                    _options.UnregisteredDatagramBurstPerIp,
                    now))
            {
                scope = "";
                return false;
            }
            scope = $"ip:{normalizedAddress}:unregistered";
            return true;
        }

        private bool TryGetRegisteredClient(byte[] data, IPEndPoint source, out Guid clientId)
        {
            clientId = Guid.Empty;
            if (data.Length < 17) return false;

            clientId = new Guid(data.AsSpan(1, 16));
            return _clients.TryGetValue(clientId, out var client) && client.EndPoint.Equals(source);
        }

        private void RemoveClientRateLimits(Guid clientId)
        {
            _audioRateLimits.TryRemove(clientId, out _);
            _controlRateLimits.TryRemove(clientId, out _);
            _transmissionTrackers.TryRemove(clientId, out _);
        }

        private bool ValidateAndTrackTransmission(ClientState sender, AudioPacket packet)
        {
            if (!float.IsFinite(packet.Frequency) || packet.Frequency < 2f || packet.Frequency > 999f)
                return false;
            if (packet.NetIdHash == null || packet.NetIdHash.Length != 8)
                return false;
            if (packet.IsEncrypted)
            {
                if (packet.Nonce is not { Length: 12 } || packet.Tag is not { Length: 16 })
                    return false;
            }
            else if (!NetIdHashIsZero(packet.NetIdHash))
            {
                return false;
            }

            bool senderMayOpenRoute = SenderIsSubscribedToRoute(sender, packet);
            if (packet.TransmissionId == 0)
                return senderMayOpenRoute;

            var tracker = _transmissionTrackers.GetOrAdd(packet.ClientId, _ => new ClientTransmissionTracker());
            return tracker.TryAccept(packet, senderMayOpenRoute, Stopwatch.GetTimestamp());
        }

        private static bool SenderIsSubscribedToRoute(ClientState sender, AudioPacket packet)
        {
            if (sender.Subscriptions.Length > 0)
            {
                var requiredHash = packet.IsEncrypted ? packet.NetIdHash : ZeroNetIdHash;
                foreach (var subscription in sender.Subscriptions)
                    if (subscription.Matches(packet.Frequency, requiredHash)) return true;
                return false;
            }

            // Accept frequency-only subscriptions on registered routes.
            foreach (var frequency in sender.Frequencies)
                if (Math.Abs(frequency - packet.Frequency) <= FrequencyTolerance) return true;
            return false;
        }

        private static bool NetIdHashIsZero(byte[] hash)
        {
            int combined = 0;
            for (int i = 0; i < hash.Length; i++) combined |= hash[i];
            return combined == 0;
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
            private bool _initialized;
            private long _lastRefillTimestamp;
            private long _lastTouchedTimestamp;
            private double _tokens;

            public bool TryAcquire(int tokensPerSecond, int burstCapacity, long nowTimestamp)
            {
                if (tokensPerSecond <= 0 || burstCapacity <= 0) return true;

                lock (_sync)
                {
                    if (!_initialized)
                    {
                        _initialized = true;
                        _tokens = burstCapacity;
                        _lastRefillTimestamp = nowTimestamp;
                    }
                    else if (nowTimestamp > _lastRefillTimestamp)
                    {
                        double elapsedSeconds = (nowTimestamp - _lastRefillTimestamp) / (double)Stopwatch.Frequency;
                        _tokens = Math.Min(burstCapacity, _tokens + elapsedSeconds * tokensPerSecond);
                        _lastRefillTimestamp = nowTimestamp;
                    }

                    _lastTouchedTimestamp = nowTimestamp;
                    if (_tokens < 1d) return false;

                    _tokens -= 1d;
                    return true;
                }
            }

            public bool IsIdle(long nowTimestamp, TimeSpan idlePeriod)
            {
                lock (_sync)
                {
                    if (!_initialized) return true;
                    double idleSeconds = (nowTimestamp - _lastTouchedTimestamp) / (double)Stopwatch.Frequency;
                    return idleSeconds >= idlePeriod.TotalSeconds;
                }
            }
        }

        private readonly record struct SourceEndpointKey(IPAddress Address, int Port);

        /// <summary>
        /// Keeps routing metadata immutable for one transmission epoch.
        /// </summary>
        private sealed class ClientTransmissionTracker
        {
            private const int MaxRetainedTransmissions = 256;
            private static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
            private static readonly TimeSpan LateMediaGrace = TimeSpan.FromSeconds(2);

            private readonly object _sync = new();
            private readonly Dictionary<ulong, TrackedTransmission> _transmissions = new();
            private long _nextPruneTimestamp;

            public bool TryAccept(AudioPacket packet, bool senderMayOpenRoute, long nowTimestamp)
            {
                lock (_sync)
                {
                    RemoveExpired(nowTimestamp);
                    if (!_transmissions.TryGetValue(packet.TransmissionId, out var tracked))
                    {
                        if (!senderMayOpenRoute || _transmissions.Count >= MaxRetainedTransmissions)
                            return false;

                        tracked = new TrackedTransmission(packet, nowTimestamp);
                        _transmissions.Add(packet.TransmissionId, tracked);
                        return true;
                    }

                    if (!tracked.RouteMatches(packet)) return false;
                    if (tracked.EndSeen)
                    {
                        if (packet.IsTransmissionEnd)
                        {
                            if (packet.Sequence != tracked.TerminalSequence) return false;
                            tracked.LastSeenTimestamp = nowTimestamp;
                            return true;
                        }

                        double sinceEndSeconds = (nowTimestamp - tracked.EndSeenTimestamp) /
                            (double)Stopwatch.Frequency;
                        if (sinceEndSeconds > LateMediaGrace.TotalSeconds ||
                            !SequenceIsAtOrBefore(packet.Sequence, tracked.TerminalSequence))
                            return false;
                    }

                    tracked.LastSeenTimestamp = nowTimestamp;
                    if (packet.IsTransmissionEnd)
                    {
                        tracked.EndSeen = true;
                        tracked.EndSeenTimestamp = nowTimestamp;
                        tracked.TerminalSequence = packet.Sequence;
                    }
                    return true;
                }
            }

            private void RemoveExpired(long nowTimestamp)
            {
                if (_transmissions.Count == 0 || nowTimestamp < _nextPruneTimestamp) return;
                _nextPruneTimestamp = nowTimestamp + Stopwatch.Frequency * 5;

                List<ulong>? expired = null;
                foreach (var entry in _transmissions)
                {
                    double idleSeconds = (nowTimestamp - entry.Value.LastSeenTimestamp) /
                        (double)Stopwatch.Frequency;
                    if (idleSeconds >= Retention.TotalSeconds)
                        (expired ??= new List<ulong>()).Add(entry.Key);
                }
                if (expired == null) return;
                foreach (var transmissionId in expired)
                    _transmissions.Remove(transmissionId);
            }

            private static bool SequenceIsAtOrBefore(ushort candidate, ushort terminal) =>
                unchecked((short)(candidate - terminal)) <= 0;

            private sealed class TrackedTransmission
            {
                private readonly int _frequencyBits;
                private readonly bool _isEncrypted;
                private readonly byte[] _netIdHash;
                private readonly string _senderName;
                private readonly string _radioName;
                private readonly uint _audioSeed;

                public long LastSeenTimestamp;
                public bool EndSeen;
                public long EndSeenTimestamp;
                public ushort TerminalSequence;

                public TrackedTransmission(AudioPacket packet, long nowTimestamp)
                {
                    _frequencyBits = BitConverter.SingleToInt32Bits(packet.Frequency);
                    _isEncrypted = packet.IsEncrypted;
                    _netIdHash = packet.NetIdHash.ToArray();
                    _senderName = packet.SenderName;
                    _radioName = packet.RadioName;
                    _audioSeed = packet.TransmissionAudioSeed;
                    LastSeenTimestamp = nowTimestamp;
                    if (packet.IsTransmissionEnd)
                    {
                        EndSeen = true;
                        EndSeenTimestamp = nowTimestamp;
                        TerminalSequence = packet.Sequence;
                    }
                }

                public bool RouteMatches(AudioPacket packet) =>
                    _frequencyBits == BitConverter.SingleToInt32Bits(packet.Frequency) &&
                    _isEncrypted == packet.IsEncrypted &&
                    NetIdHashesEqual(_netIdHash, packet.NetIdHash) &&
                    string.Equals(_senderName, packet.SenderName, StringComparison.Ordinal) &&
                    string.Equals(_radioName, packet.RadioName, StringComparison.Ordinal) &&
                    _audioSeed == packet.TransmissionAudioSeed;

                private static bool NetIdHashesEqual(byte[] left, byte[] right)
                {
                    if (left.Length != right.Length) return false;
                    int combined = 0;
                    for (int i = 0; i < left.Length; i++) combined |= left[i] ^ right[i];
                    return combined == 0;
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

        /// <summary>
        /// Confirms bidirectional UDP reachability to the client.
        /// </summary>
        private void SendAck(Guid clientId, IPEndPoint to)
        {
            try
            {
                var ack = new HeartbeatPacket { ClientId = clientId }.Encode(PacketType.HeartbeatAck);
                SendDatagram(ack, to);
            }
            catch
            {
                // Let the client health timeout report failed acknowledgements.
            }
        }

        private void BroadcastPresenceUpdate()
        {
            var clients = _clients.Values.ToArray();
            static PresenceSubscription[] SubscriptionsFor(ClientState client) =>
                client.Subscriptions.Length > 0
                    ? client.Subscriptions
                    : Array.ConvertAll(client.Frequencies,
                        frequency => new PresenceSubscription { Frequency = frequency, NetIdHash = new byte[8] });
            static string DisplayCallsign(ClientState client) =>
                string.IsNullOrWhiteSpace(client.Callsign) ? "(no callsign)" : client.Callsign.Trim();

            var counts = PresenceCounter.Build(clients.SelectMany(SubscriptionsFor));
            for (int i = 0; i < counts.Length; i++)
            {
                var count = counts[i];
                var memberNames = clients
                    .Where(client => SubscriptionsFor(client).Any(subscription =>
                        subscription.Matches(count.Frequency, count.NetIdHash)))
                    .Select(DisplayCallsign)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                counts[i] = new PresenceChannelCount
                {
                    Frequency = count.Frequency,
                    NetIdHash = count.NetIdHash,
                    // Count distinct connected clients, not subscriptions, so
                    // one person tuning two radios identically appears once.
                    UserCount = memberNames.Length,
                    ClientNames = memberNames
                };
            }
            var data = new PresenceUpdatePacket
            {
                Counts = counts,
                TotalUserCount = clients.Length,
                ConnectedClientNames = clients
                    .Select(DisplayCallsign)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(name => name, StringComparer.Ordinal)
                    .ToArray()
            }.Encode();

            foreach (var client in _clients.Values)
            {
                try { SendDatagram(data, client.EndPoint); }
                catch
                {
                    // Clients will receive the next presence update.
                }
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

                // Relay encrypted carriers to all tuned receivers for RF occupancy modeling.
                if (!ClientIsTunedToFrequency(state, packet.Frequency)) continue;

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

        private static bool ClientIsTunedToFrequency(ClientState state, float frequency)
        {
            if (state.Subscriptions.Length > 0)
            {
                foreach (var subscription in state.Subscriptions)
                    if (Math.Abs(subscription.Frequency - frequency) <= FrequencyTolerance) return true;
                return false;
            }

            foreach (var tunedFrequency in state.Frequencies)
                if (Math.Abs(tunedFrequency - frequency) <= FrequencyTolerance) return true;
            return false;
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
                        RemoveClientRateLimits(kvp.Key);
                        var name = string.IsNullOrWhiteSpace(removed.Callsign) ? "(no callsign)" : removed.Callsign;
                        Console.WriteLine($"[Disconnect] {name} timed out");
                        BroadcastPresenceUpdate();
                    }
                }

                RemoveIdleUnregisteredRateLimits();
            }
        }

        private void RemoveIdleUnregisteredRateLimits()
        {
            long now = Stopwatch.GetTimestamp();
            var idlePeriod = TimeSpan.FromMinutes(5);
            foreach (var entry in _unregisteredEndpointRateLimits)
                if (entry.Value.IsIdle(now, idlePeriod))
                    _unregisteredEndpointRateLimits.TryRemove(entry.Key, out _);
            foreach (var entry in _unregisteredIpRateLimits)
                if (entry.Value.IsIdle(now, idlePeriod))
                    _unregisteredIpRateLimits.TryRemove(entry.Key, out _);
        }
    }
}
