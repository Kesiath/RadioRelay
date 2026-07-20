using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// <summary>
    /// Moves protocol packets over UDP and tracks bidirectional server health.
    /// Audio encoding and encryption remain in <c>AudioEngine</c>.
    /// </summary>
    public class RelayClient : IDisposable
    {
        // Tolerate one missed heartbeat before reporting an unhealthy connection.
        private const int HealthTimeoutSeconds = 12;
        private const int MaxQueuedAudioDatagrams = 12;
        private static readonly long MaxQueuedAudioAgeTicks = Stopwatch.Frequency * 60 / 1000;

        private readonly record struct PendingAudioDatagram(
            byte[] Data,
            float Frequency,
            long EnqueuedTimestamp);
        private readonly record struct AudioSendFailure(Exception Exception, float Frequency);
        private readonly record struct HealthTransition(bool Changed, bool Healthy, long Generation);

        private UdpClient? _udp;
        private CancellationTokenSource? _cts;
        private System.Threading.Timer? _heartbeatTimer;
        private System.Threading.Timer? _healthCheckTimer;
        private DateTime _lastAckReceived;
        private volatile bool _isHealthy;
        private bool _hasReportedHealthState;
        private readonly object _healthSync = new();
        private readonly object _healthPublishSync = new();
        private long _healthGeneration;
        private readonly ConcurrentQueue<PendingAudioDatagram> _audioSendQueue = new();
        private readonly AutoResetEvent _audioSendSignal = new(false);
        private readonly object _audioSendSync = new();
        private Thread? _audioSendThread;
        private volatile bool _audioSendStopping;
        private int _queuedAudioDatagrams;
        private bool _disposed;
        private readonly object _subscriptionSync = new();
        private PresenceSubscription[] _lastSubscriptions = Array.Empty<PresenceSubscription>();
        private readonly IClientDiagnostics? _diagnostics;
        private readonly NetworkQualityTracker _networkQuality = new();
        private RelayConnectionState _connectionState = RelayConnectionState.Disconnected;

        public Guid ClientId { get; }
        public bool IsConnected { get; private set; }
        public bool IsHealthy => _isHealthy;
        public string ServerPassword { get; set; } = "";
        public NetworkQualitySnapshot QualitySnapshot =>
            _networkQuality.Snapshot(DateTime.UtcNow, _connectionState);

        /// <summary>
        /// Callsign sent for presence and server logging, never access control.
        /// </summary>
        public string Callsign { get; set; } = "";

        public event Action<AudioPacket>? AudioReceived;
        public event Action<PresenceChannelCount[]>? PresenceUpdated;
        public event Action<int>? TotalUserCountUpdated;
        public event Action<string[]>? ConnectedClientNamesUpdated;
        public event Action<string>? StatusChanged;

        /// <summary>
        /// Reports confirmed UDP reachability transitions.
        /// </summary>
        public event Action<bool>? ConnectionHealthChanged;
        public event Action<NetworkQualitySnapshot>? NetworkQualityChanged;

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
            ObjectDisposedException.ThrowIf(_disposed, this);
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
                lock (_healthPublishSync)
                {
                    lock (_healthSync)
                    {
                        IsConnected = true;
                        _isHealthy = false;
                        _hasReportedHealthState = false;
                        _lastAckReceived = DateTime.MinValue; // Unhealthy until the server acknowledges.
                        _healthGeneration++;
                    }
                    SetConnectionState(RelayConnectionState.Connecting);
                }
                EnsureAudioSendThread();

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
                lock (_healthPublishSync)
                {
                    lock (_healthSync)
                    {
                        IsConnected = false;
                        _isHealthy = false;
                        _hasReportedHealthState = false;
                        _healthGeneration++;
                    }
                    SetConnectionState(RelayConnectionState.Disconnected);
                }
                throw;
            }
        }

        public void Disconnect()
        {
            if (!IsConnected) return;
            _heartbeatTimer?.Dispose();
            _healthCheckTimer?.Dispose();

            FlushPendingAudioDatagrams();
            SendPresenceDisconnect();
            lock (_healthPublishSync)
            {
                lock (_healthSync)
                {
                    IsConnected = false;
                    _isHealthy = false;
                    _hasReportedHealthState = false;
                    _healthGeneration++;
                }
                SetConnectionState(RelayConnectionState.Disconnected);
            }

            _cts?.Cancel();
            _udp?.Close();
            ClearPendingAudioDatagrams();
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
                _networkQuality.RecordHeartbeatSent(DateTime.UtcNow);
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

        /// <summary>
        /// Stamps the client ID and queues audio for ordered UDP delivery.
        /// </summary>
        public void SendAudio(AudioPacket packet)
        {
            if (_udp == null || !IsConnected) return;
            packet.ClientId = ClientId;
            try
            {
                var data = packet.Encode();
                var pending = new PendingAudioDatagram(
                    data,
                    packet.Frequency,
                    Stopwatch.GetTimestamp());
                if (packet.IsTransmissionEnd)
                {
                    AudioSendFailure? failure;
                    lock (_audioSendSync)
                    {
                        failure = DrainPendingAudioDatagramsLocked();
                        if (failure == null)
                            failure = TrySendAudioDatagramLocked(pending);
                    }
                    if (failure != null) ReportAudioSendFailure(failure.Value);
                    return;
                }

                _audioSendQueue.Enqueue(pending);
                Interlocked.Increment(ref _queuedAudioDatagrams);
                while (Volatile.Read(ref _queuedAudioDatagrams) > MaxQueuedAudioDatagrams &&
                       _audioSendQueue.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _queuedAudioDatagrams);
                }
                _audioSendSignal.Set();
            }
            catch (Exception ex)
            {
                // Report an explicit send failure without waiting for the health timer.
                ReportAudioSendFailure(new AudioSendFailure(ex, packet.Frequency));
            }
        }

        private void SendHeartbeat()
        {
            if (_udp == null || !IsConnected) return;

            // Resubscribe to restore server state after a restart.
            var subscriptions = GetLastSubscriptionsSnapshot();
            if (_isHealthy && subscriptions.Length > 0)
            {
                SendSubscribeSnapshot(subscriptions);
                return;
            }

            try
            {
                var data = new HeartbeatPacket { ClientId = ClientId, ServerPassword = ServerPassword }.Encode();
                _networkQuality.RecordHeartbeatSent(DateTime.UtcNow);
                _udp.Send(data, data.Length);
            }
            catch
            {
                // Let the health check report persistent socket failures.
            }
        }

        private void CheckHealth()
        {
            if (!IsConnected) return;
            lock (_healthPublishSync)
            {
                HealthTransition transition;
                lock (_healthSync)
                {
                    if (!IsConnected) return;
                    bool healthy = (DateTime.UtcNow - _lastAckReceived).TotalSeconds <= HealthTimeoutSeconds;
                    transition = UpdateHealthyLocked(healthy);
                }
                PublishHealthTransitionLocked(transition);
            }
        }

        private void EnsureAudioSendThread()
        {
            if (_audioSendThread != null) return;
            _audioSendStopping = false;
            _audioSendThread = new Thread(ProcessAudioSendQueue)
            {
                IsBackground = true,
                Name = "RadioRelay UDP audio sender",
                Priority = ThreadPriority.AboveNormal
            };
            _audioSendThread.Start();
        }

        private void ProcessAudioSendQueue()
        {
            while (!_audioSendStopping)
            {
                _audioSendSignal.WaitOne();
                if (_audioSendStopping) break;
                AudioSendFailure? failure;
                lock (_audioSendSync)
                    failure = DrainPendingAudioDatagramsLocked();
                if (failure != null) ReportAudioSendFailure(failure.Value);
            }
        }

        private void FlushPendingAudioDatagrams()
        {
            AudioSendFailure? failure;
            lock (_audioSendSync)
                failure = DrainPendingAudioDatagramsLocked();
            if (failure != null) ReportAudioSendFailure(failure.Value);
        }

        private AudioSendFailure? DrainPendingAudioDatagramsLocked()
        {
            while (_audioSendQueue.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _queuedAudioDatagrams);
                if (Stopwatch.GetTimestamp() - pending.EnqueuedTimestamp > MaxQueuedAudioAgeTicks)
                    continue;

                var failure = TrySendAudioDatagramLocked(pending);
                if (failure != null)
                {
                    ClearPendingAudioDatagrams();
                    return failure;
                }
            }
            return null;
        }

        private AudioSendFailure? TrySendAudioDatagramLocked(PendingAudioDatagram pending)
        {
            var udp = _udp;
            if (udp == null || !IsConnected) return null;
            try
            {
                udp.Send(pending.Data, pending.Data.Length);
                return null;
            }
            catch (Exception ex)
            {
                ClearPendingAudioDatagrams();
                return new AudioSendFailure(ex, pending.Frequency);
            }
        }

        private void ReportAudioSendFailure(AudioSendFailure failure)
        {
            RaiseStatusChanged($"Failed to send audio: {failure.Exception.Message}");
            _diagnostics?.LogException(
                ErrorCodes.ClientSendAudioFailure,
                $"send audio frequency={failure.Frequency:0.000}",
                failure.Exception);
            SetHealthy(false);
        }

        private void ClearPendingAudioDatagrams()
        {
            while (_audioSendQueue.TryDequeue(out _))
                Interlocked.Decrement(ref _queuedAudioDatagrams);
            Interlocked.Exchange(ref _queuedAudioDatagrams, 0);
        }

        private void SetHealthy(bool healthy)
        {
            lock (_healthPublishSync)
            {
                HealthTransition transition;
                lock (_healthSync)
                {
                    if (!IsConnected) return;
                    transition = UpdateHealthyLocked(healthy);
                }
                PublishHealthTransitionLocked(transition);
            }
        }

        private HealthTransition UpdateHealthyLocked(bool healthy)
        {
            if (_hasReportedHealthState && healthy == _isHealthy)
                return new HealthTransition(false, healthy, _healthGeneration);
            _isHealthy = healthy;
            _hasReportedHealthState = true;
            return new HealthTransition(true, healthy, ++_healthGeneration);
        }

        private void PublishHealthTransitionLocked(HealthTransition transition)
        {
            if (!transition.Changed) return;

            // Serialize publication and reject transitions invalidated by reconnection.
            lock (_healthSync)
            {
                if (transition.Generation != _healthGeneration ||
                    !_hasReportedHealthState ||
                    transition.Healthy != _isHealthy)
                {
                    return;
                }
            }

            bool healthy = transition.Healthy;
            SetConnectionState(healthy ? RelayConnectionState.Connected : RelayConnectionState.Unhealthy);

            // Allow terminal PTT controls before deregistering the endpoint.
            SafeInvoke(ConnectionHealthChanged, healthy);

            // Withdraw presence but retain probes so a later acknowledgement can recover.
            if (!healthy)
                SendPresenceDisconnect();
        }

        private void SendPresenceDisconnect()
        {
            try
            {
                var data = new HeartbeatPacket { ClientId = ClientId, ServerPassword = ServerPassword }.Encode(PacketType.Disconnect);
                _udp?.Send(data, data.Length);
            }
            catch
            {
                // Let the server expire the client if disconnect delivery fails.
            }
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
                    var receivedUtc = DateTime.UtcNow;
                    _networkQuality.RecordPacket(receivedUtc);
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

                DateTime receivedUtc = DateTime.UtcNow;
                lock (_healthPublishSync)
                {
                    HealthTransition transition;
                    lock (_healthSync)
                    {
                        if (!IsConnected) return;
                        _lastAckReceived = receivedUtc;
                        transition = UpdateHealthyLocked(true);
                    }
                    _networkQuality.RecordAck(receivedUtc);
                    PublishHealthTransitionLocked(transition);
                }
                return;
            }

            if (type == PacketType.PresenceUpdate)
            {
                var presence = PresenceUpdatePacket.Decode(data);
                SafeInvoke(PresenceUpdated, presence.Counts);
                SafeInvoke(TotalUserCountUpdated, presence.TotalUserCount);
                SafeInvoke(ConnectedClientNamesUpdated, presence.ConnectedClientNames);
                return;
            }

            if (type != PacketType.Audio) return;

            var packet = AudioPacket.Decode(data);
            if (packet.ClientId == ClientId) return; // Ignore reflected local packets.
            _networkQuality.RecordInboundAudio(
                packet.ClientId,
                packet.TransmissionId,
                packet.Sequence,
                DateTime.UtcNow);
            SafeInvoke(AudioReceived, packet);
        }

        private void SetConnectionState(RelayConnectionState state)
        {
            _connectionState = state;
            SafeInvoke(NetworkQualityChanged, QualitySnapshot);
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
                catch
                {
                    // Isolate network loops and timers from subscriber failures.
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _audioSendStopping = true;
            _audioSendSignal.Set();
            var thread = _audioSendThread;
            _audioSendThread = null;
            if (thread != null && thread != Thread.CurrentThread)
                thread.Join(TimeSpan.FromSeconds(1));
            _audioSendSignal.Dispose();
        }
    }
}
