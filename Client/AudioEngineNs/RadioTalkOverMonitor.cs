using System;
using System.Collections.Generic;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Detects the start of local and remote transmission overlap on one receiver.
    /// </summary>
    internal sealed class RadioTalkOverMonitor
    {
        private readonly HashSet<RadioTransmissionKey> _remoteTransmitters = new();
        private bool _hasWarnedForCurrentOverlap;

        public bool IsLocalTransmitting { get; private set; }
        public bool HasRemoteTransmitters => _remoteTransmitters.Count > 0;
        public bool HasActiveOverlap => IsLocalTransmitting && HasRemoteTransmitters;

        public bool IsRemoteTransmitting(Guid senderId) =>
            IsRemoteTransmitting(new RadioTransmissionKey(senderId, 0));

        public bool IsRemoteTransmitting(RadioTransmissionKey senderId) =>
            _remoteTransmitters.Contains(senderId);

        public bool SetLocalTransmitting(bool active)
        {
            if (active == IsLocalTransmitting) return false;

            IsLocalTransmitting = active;
            if (!active)
            {
                _hasWarnedForCurrentOverlap = false;
                return false;
            }

            return TryEnterOverlap();
        }

        public bool ObserveRemoteTransmissionStart(Guid senderId) =>
            ObserveRemoteTransmissionStart(new RadioTransmissionKey(senderId, 0));

        public bool ObserveRemoteTransmissionStart(RadioTransmissionKey senderId)
        {
            if (!_remoteTransmitters.Add(senderId)) return false;
            return TryEnterOverlap();
        }

        public void ObserveRemoteTransmissionEnd(Guid senderId) =>
            ObserveRemoteTransmissionEnd(new RadioTransmissionKey(senderId, 0));

        public void ObserveRemoteTransmissionEnd(RadioTransmissionKey senderId)
        {
            _remoteTransmitters.Remove(senderId);
            if (_remoteTransmitters.Count == 0)
                _hasWarnedForCurrentOverlap = false;
        }

        public void ClearRemoteTransmitters()
        {
            _remoteTransmitters.Clear();
            _hasWarnedForCurrentOverlap = false;
        }

        public void Reset()
        {
            _remoteTransmitters.Clear();
            IsLocalTransmitting = false;
            _hasWarnedForCurrentOverlap = false;
        }

        private bool TryEnterOverlap()
        {
            if (!IsLocalTransmitting || _remoteTransmitters.Count == 0 || _hasWarnedForCurrentOverlap)
                return false;

            _hasWarnedForCurrentOverlap = true;
            return true;
        }
    }
}
