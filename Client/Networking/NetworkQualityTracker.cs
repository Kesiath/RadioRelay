using System;
using System.Collections.Generic;

namespace RadioRelay.Client.Networking
{
    public enum RelayConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Unhealthy
    }

    public readonly record struct NetworkQualitySnapshot(
        RelayConnectionState ConnectionState,
        int PacketLossPercent,
        TimeSpan? LastServerAckAge,
        int PacketsPerSecond,
        double JitterMs);

    /// <summary>
    /// Accumulates thread-safe relay transport telemetry.
    /// </summary>
    public sealed class NetworkQualityTracker
    {
        private static readonly TimeSpan PacketRateWindow = TimeSpan.FromSeconds(1);
        private readonly object _sync = new();
        private readonly Queue<DateTime> _recentPackets = new();
        private long _heartbeatsSent;
        private long _acksReceived;
        private DateTime? _lastAckUtc;
        private DateTime? _lastAudioArrivalUtc;
        private ushort? _lastAudioSequence;
        private Guid? _lastAudioClientId;
        private ulong? _lastTransmissionId;
        private double _jitterMs;

        public void RecordHeartbeatSent(DateTime utcNow)
        {
            lock (_sync)
                _heartbeatsSent++;
        }

        public void RecordAck(DateTime utcNow)
        {
            lock (_sync)
            {
                _acksReceived++;
                _lastAckUtc = utcNow;
            }
        }

        public void RecordPacket(DateTime utcNow)
        {
            lock (_sync)
            {
                _recentPackets.Enqueue(utcNow);
                TrimPacketWindow(utcNow);
            }
        }

        public void RecordInboundAudio(ushort sequence, DateTime utcNow) =>
            RecordInboundAudio(Guid.Empty, 0, sequence, utcNow);

        public void RecordInboundAudio(
            Guid clientId,
            ulong transmissionId,
            ushort sequence,
            DateTime utcNow)
        {
            lock (_sync)
            {
                bool sameStream = _lastAudioClientId == clientId &&
                    _lastTransmissionId == transmissionId;
                if (sameStream && _lastAudioArrivalUtc.HasValue && _lastAudioSequence.HasValue)
                {
                    int sequenceDelta = unchecked((ushort)(sequence - _lastAudioSequence.Value));
                    if (sequenceDelta > 0 && sequenceDelta < 128)
                    {
                        double actualMs = (utcNow - _lastAudioArrivalUtc.Value).TotalMilliseconds;
                        double expectedMs = sequenceDelta * 20.0;
                        _jitterMs = Math.Abs(actualMs - expectedMs);
                    }
                }

                _lastAudioArrivalUtc = utcNow;
                _lastAudioSequence = sequence;
                _lastAudioClientId = clientId;
                _lastTransmissionId = transmissionId;
            }
        }

        public NetworkQualitySnapshot Snapshot(DateTime utcNow, RelayConnectionState connectionState)
        {
            lock (_sync)
            {
                TrimPacketWindow(utcNow);
                int packetLoss = _heartbeatsSent <= 0
                    ? 0
                    : (int)Math.Clamp(
                        Math.Round(100.0 * Math.Max(0, _heartbeatsSent - _acksReceived) / _heartbeatsSent),
                        0,
                        100);
                TimeSpan? ackAge = _lastAckUtc.HasValue
                    ? TimeSpan.FromTicks(Math.Max(0, (utcNow - _lastAckUtc.Value).Ticks))
                    : null;

                return new NetworkQualitySnapshot(
                    connectionState,
                    packetLoss,
                    ackAge,
                    _recentPackets.Count,
                    _jitterMs);
            }
        }

        private void TrimPacketWindow(DateTime utcNow)
        {
            DateTime cutoff = utcNow - PacketRateWindow;
            while (_recentPackets.Count > 0 && _recentPackets.Peek() < cutoff)
                _recentPackets.Dequeue();
        }
    }
}
