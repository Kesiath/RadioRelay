using System;
using System.Collections.Generic;

namespace RadioRelay.Client.AudioEngineNs
{
    /// 
    /// Tracks whether this local user is transmitting while any audible remote
    /// user is also keyed on the same receiver. It returns true only on the
    /// transition into overlap, so the talk-over warning plays once per
    /// overlap episode instead of looping for every audio packet.
    /// 
    internal sealed class RadioTalkOverMonitor
    {
        private readonly HashSet<Guid> _remoteTransmitters = new();
        private bool _hasWarnedForCurrentOverlap;

        public bool IsLocalTransmitting { get; private set; }
        public bool HasRemoteTransmitters => _remoteTransmitters.Count > 0;
        public bool HasActiveOverlap => IsLocalTransmitting && HasRemoteTransmitters;

        public bool IsRemoteTransmitting(Guid senderId) => _remoteTransmitters.Contains(senderId);

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

        public bool ObserveRemoteTransmissionStart(Guid senderId)
        {
            if (!_remoteTransmitters.Add(senderId)) return false;
            return TryEnterOverlap();
        }

        public void ObserveRemoteTransmissionEnd(Guid senderId)
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
