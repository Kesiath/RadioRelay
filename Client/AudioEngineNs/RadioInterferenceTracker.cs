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

    /// <summary>
    /// Tracks FM capture and co-channel interference for one receiver.
    /// </summary>
    internal sealed class RadioInterferenceTracker
    {
        private readonly HashSet<RadioTransmissionKey> _activeSenders = new();
        private RadioTransmissionKey? _primarySenderId;

        public bool HasPrimarySender => _primarySenderId.HasValue;

        public bool HasInterference => _primarySenderId.HasValue && _activeSenders.Count > 1;

        public InterferenceStartDecision ObserveTransmissionStart(Guid senderId) =>
            ObserveTransmissionStart(new RadioTransmissionKey(senderId, 0));

        public InterferenceStartDecision ObserveTransmissionStart(RadioTransmissionKey senderId)
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

        public InterferenceStartDecision ObserveMidStreamTransmission(RadioTransmissionKey senderId) =>
            ObserveTransmissionStart(senderId);

        public InterferenceEndDecision ObserveTransmissionEnd(Guid senderId) =>
            ObserveTransmissionEnd(new RadioTransmissionKey(senderId, 0));

        public InterferenceEndDecision ObserveTransmissionEnd(RadioTransmissionKey senderId)
        {
            _activeSenders.Remove(senderId);
            bool endedPrimary = _primarySenderId == senderId;
            if (endedPrimary)
                _primarySenderId = null;

            return new InterferenceEndDecision { EndedPrimarySender = endedPrimary };
        }

        public bool ShouldAcceptAudioFrom(Guid senderId) =>
            ShouldAcceptAudioFrom(new RadioTransmissionKey(senderId, 0));

        public bool ShouldAcceptAudioFrom(RadioTransmissionKey senderId) => _primarySenderId == senderId;

        public bool IsActive(RadioTransmissionKey senderId) => _activeSenders.Contains(senderId);

        public void Reset()
        {
            _activeSenders.Clear();
            _primarySenderId = null;
        }
    }
}
