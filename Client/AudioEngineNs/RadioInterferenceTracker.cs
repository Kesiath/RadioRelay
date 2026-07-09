using System;
using System.Collections.Generic;

namespace RadioRelay.Client.AudioEngineNs
{
    internal readonly struct InterferenceStartDecision
    {
        public bool AcceptAudio { get; init; }
        public bool IsPrimarySender { get; init; }
        public bool IsInterferingSender { get; init; }
    }

    internal readonly struct InterferenceEndDecision
    {
        public bool EndedPrimarySender { get; init; }
    }

    /// 
    /// Models simple FM-style co-channel behavior for one receiver: the first
    /// audible transmitter captures the receiver, later overlapping
    /// transmitters create interference/noise but do not play as independent
    /// local squelch sounds or take over the jitter stream mid-transmission.
    /// 
    internal sealed class RadioInterferenceTracker
    {
        private readonly HashSet<Guid> _activeSenders = new();
        private Guid? _primarySenderId;

        public bool HasPrimarySender => _primarySenderId.HasValue;

        public bool HasInterference => _primarySenderId.HasValue && _activeSenders.Count > 1;

        public InterferenceStartDecision ObserveTransmissionStart(Guid senderId)
        {
            _activeSenders.Add(senderId);

            if (_primarySenderId == null)
            {
                _primarySenderId = senderId;
                return new InterferenceStartDecision
                {
                    AcceptAudio = true,
                    IsPrimarySender = true,
                    IsInterferingSender = false
                };
            }

            bool isPrimary = _primarySenderId == senderId;
            return new InterferenceStartDecision
            {
                AcceptAudio = isPrimary,
                IsPrimarySender = isPrimary,
                IsInterferingSender = !isPrimary
            };
        }

        public InterferenceStartDecision ObserveMidStreamTransmission(Guid senderId) =>
            ObserveTransmissionStart(senderId);

        public InterferenceEndDecision ObserveTransmissionEnd(Guid senderId)
        {
            _activeSenders.Remove(senderId);
            bool endedPrimary = _primarySenderId == senderId;
            if (endedPrimary)
                _primarySenderId = null;

            return new InterferenceEndDecision { EndedPrimarySender = endedPrimary };
        }

        public bool ShouldAcceptAudioFrom(Guid senderId) => _primarySenderId == senderId;

        public void Reset()
        {
            _activeSenders.Clear();
            _primarySenderId = null;
        }
    }
}
