using System.Collections.Generic;
using RadioRelay.Client.Radio;

namespace RadioRelay.Client.UI
{
    internal enum RadioActivityKind
    {
        Idle,
        Receiving,
        Transmitting
    }

    /// 
    /// Tracks user-visible per-radio activity from transmission lifecycle events.
    /// Remote receive state is keyed by stable remote client id instead of an
    /// increment/decrement counter so duplicate starts, duplicate ends, and
    /// near-simultaneous multi-user ends cannot leave a stale RX indicator.
    /// 
    internal sealed class RadioActivityTracker
    {
        private readonly HashSet<RadioChannel> _localTransmitChannels = new();
        private readonly Dictionary<RadioChannel, HashSet<string>> _remoteReceiversByChannel = new();
        private readonly Dictionary<RadioChannel, long> _lastLocalLifecycleByChannel = new();
        private readonly Dictionary<RadioChannel, Dictionary<string, long>> _lastRemoteLifecycleByChannel = new();

        public void LocalStarted(RadioChannel channel, long? lifecycleSequence = null)
        {
            if (!AcceptLocalLifecycle(channel, lifecycleSequence)) return;
            _localTransmitChannels.Add(channel);
        }

        public void LocalEnded(RadioChannel channel, long? lifecycleSequence = null)
        {
            if (!AcceptLocalLifecycle(channel, lifecycleSequence)) return;
            _localTransmitChannels.Remove(channel);
        }

        public void RemoteStarted(RadioChannel channel, string remoteClientId, long? lifecycleSequence = null)
        {
            string key = NormalizeRemoteClientId(remoteClientId);
            if (key.Length == 0) return;
            if (!AcceptRemoteLifecycle(channel, key, lifecycleSequence)) return;

            if (!_remoteReceiversByChannel.TryGetValue(channel, out var remotes))
            {
                remotes = new HashSet<string>();
                _remoteReceiversByChannel[channel] = remotes;
            }

            remotes.Add(key);
        }

        public void RemoteEnded(RadioChannel channel, string remoteClientId, long? lifecycleSequence = null)
        {
            string key = NormalizeRemoteClientId(remoteClientId);
            if (key.Length > 0 && !AcceptRemoteLifecycle(channel, key, lifecycleSequence)) return;

            if (!_remoteReceiversByChannel.TryGetValue(channel, out var remotes)) return;

            if (key.Length == 0)
                remotes.Clear();
            else
                remotes.Remove(key);

            if (remotes.Count == 0)
                _remoteReceiversByChannel.Remove(channel);
        }

        public bool IsReceiving(RadioChannel channel) =>
            _remoteReceiversByChannel.TryGetValue(channel, out var remotes) && remotes.Count > 0;

        public RadioActivityKind GetActivity(RadioChannel channel)
        {
            if (_localTransmitChannels.Contains(channel)) return RadioActivityKind.Transmitting;
            return IsReceiving(channel) ? RadioActivityKind.Receiving : RadioActivityKind.Idle;
        }

        private static string NormalizeRemoteClientId(string remoteClientId) =>
            string.IsNullOrWhiteSpace(remoteClientId) ? string.Empty : remoteClientId.Trim();

        private bool AcceptLocalLifecycle(RadioChannel channel, long? lifecycleSequence)
        {
            if (lifecycleSequence == null) return true;
            if (_lastLocalLifecycleByChannel.TryGetValue(channel, out var lastSeen) && lifecycleSequence.Value <= lastSeen)
                return false;

            _lastLocalLifecycleByChannel[channel] = lifecycleSequence.Value;
            return true;
        }

        private bool AcceptRemoteLifecycle(RadioChannel channel, string remoteClientId, long? lifecycleSequence)
        {
            if (lifecycleSequence == null) return true;
            if (!_lastRemoteLifecycleByChannel.TryGetValue(channel, out var lastByRemote))
            {
                lastByRemote = new Dictionary<string, long>();
                _lastRemoteLifecycleByChannel[channel] = lastByRemote;
            }

            if (lastByRemote.TryGetValue(remoteClientId, out var lastSeen) && lifecycleSequence.Value <= lastSeen)
                return false;

            lastByRemote[remoteClientId] = lifecycleSequence.Value;
            return true;
        }
    }
}
