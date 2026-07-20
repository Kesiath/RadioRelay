using System;
using System.Collections.Generic;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Describes one jitter-buffer playout tick and its lifecycle boundaries.
    /// </summary>
    public readonly struct JitterTickResult
    {
        public short[]? Pcm { get; init; }
        public bool IsFirstFrame { get; init; }
        public bool IsLastFrame { get; init; }

        public static readonly JitterTickResult Empty = new();
    }

    /// <summary>
    /// Controls whether a packet without Start may establish buffered or immediate playout.
    /// </summary>
    public enum JitterAcquisitionMode
    {
        None,
        Buffered,
        Immediate
    }

    /// <summary>
    /// Reorders Opus frames for steady 20 ms playout while isolating transmission epochs.
    /// </summary>
    public class JitterBuffer
    {
        private const int TargetDepthFrames = 3; // About 60 ms before normal playout.
        private const int MaxConcealedInARow = 25; // About 500 ms before stopping.
        private const int MaxBufferedFrames = 128; // 2.56-second memory bound.
        private const int MaxFutureDistanceFrames = 128;
        private const int MaxRetiredTransmissionIds = 32;

        private readonly Dictionary<ushort, byte[]> _frames = new();
        private readonly HashSet<ulong> _retiredTransmissionIds = new();
        private readonly Queue<ulong> _retiredTransmissionOrder = new();
        private readonly OpusCodec _opus;
        private readonly object _sync = new();

        private ushort _nextExpected;
        private ushort? _terminalSequence;
        private ulong _activeTransmissionId;
        private bool _hasActiveTransmission;
        private bool _started;
        private bool _idle = true;
        private bool _playImmediately;
        private bool _endOnlyAcquisition;
        private int _prebufferTicksAfterEnd;
        private int _concealedInARow;

        public JitterBuffer(OpusCodec opus) => _opus = opus;

        /// <summary>
        /// Current bounded buffer occupancy for diagnostics and tests.
        /// </summary>
        public int BufferedFrameCount
        {
            get { lock (_sync) return _frames.Count; }
        }

        public ulong ActiveTransmissionId
        {
            get { lock (_sync) return _hasActiveTransmission ? _activeTransmissionId : 0; }
        }

        public void Reset()
        {
            lock (_sync)
            {
                // Preserve the epoch so the same PTT can be reacquired after a local reset.
                ResetPlaybackNoLock();
            }
        }

        /// <summary>
        /// Accepts packets without a transmission ID using optional immediate restart behavior.
        /// </summary>
        public void OnFrameReceived(
            ushort sequence,
            byte[] opusBytes,
            bool isStart,
            bool isEnd,
            bool forceStart = false)
        {
            lock (_sync)
            {
                OnFrameReceivedNoLock(
                    transmissionId: 0,
                    sequence,
                    opusBytes,
                    isStart,
                    isEnd,
                    forceStart ? JitterAcquisitionMode.Immediate : JitterAcquisitionMode.None,
                    forceLegacyRestart: forceStart);
            }
        }

        /// <summary>
        /// Accepts an epoch-aware frame when it is valid for the bounded reorder window.
        /// </summary>
        public bool OnFrameReceived(
            ulong transmissionId,
            ushort sequence,
            byte[] opusBytes,
            bool isStart,
            bool isEnd,
            JitterAcquisitionMode acquisitionMode = JitterAcquisitionMode.None)
        {
            lock (_sync)
            {
                return OnFrameReceivedNoLock(
                    transmissionId,
                    sequence,
                    opusBytes,
                    isStart,
                    isEnd,
                    acquisitionMode,
                    forceLegacyRestart: false);
            }
        }

        private bool OnFrameReceivedNoLock(
            ulong transmissionId,
            ushort sequence,
            byte[] opusBytes,
            bool isStart,
            bool isEnd,
            JitterAcquisitionMode acquisitionMode,
            bool forceLegacyRestart)
        {
            ArgumentNullException.ThrowIfNull(opusBytes);

            bool openedStream = false;
            bool sameEpoch = HasSameActiveEpochNoLock(transmissionId);

            if (forceLegacyRestart)
            {
                BeginStreamNoLock(transmissionId, sequence, playImmediately: true);
                openedStream = true;
            }
            else if (isStart)
            {
                if (transmissionId != 0 && _retiredTransmissionIds.Contains(transmissionId))
                    return false;

                // Ignore duplicate starts after decoder state advances.
                if (!sameEpoch || transmissionId == 0)
                {
                    BeginStreamNoLock(
                        transmissionId,
                        sequence,
                        playImmediately: acquisitionMode == JitterAcquisitionMode.Immediate);
                    openedStream = true;
                }
                else if (!_started)
                {
                    // Extend an unplayed prebuffer to an earlier reordered start hint.
                    int earlierDistance = ForwardDistance(sequence, _nextExpected);
                    if (earlierDistance > 0 && earlierDistance <= MaxFutureDistanceFrames)
                        _nextExpected = sequence;
                }
            }
            else if (_idle)
            {
                if (acquisitionMode == JitterAcquisitionMode.None ||
                    (transmissionId != 0 && _retiredTransmissionIds.Contains(transmissionId)))
                    return false;

                BeginStreamNoLock(
                    transmissionId,
                    sequence,
                    playImmediately: acquisitionMode == JitterAcquisitionMode.Immediate);
                openedStream = true;
            }
            else if (!sameEpoch)
            {
                if (acquisitionMode == JitterAcquisitionMode.None ||
                    (transmissionId != 0 && _retiredTransmissionIds.Contains(transmissionId)))
                    return false;

                BeginStreamNoLock(
                    transmissionId,
                    sequence,
                    playImmediately: acquisitionMode == JitterAcquisitionMode.Immediate);
                openedStream = true;
            }

            bool accepted = openedStream;

            if (isEnd)
            {
                // Keep the first authenticated terminal sequence.
                if (_terminalSequence.HasValue && _terminalSequence.Value != sequence)
                    return false;

                _terminalSequence = sequence;
                _endOnlyAcquisition = openedStream && opusBytes.Length == 0;
                RemoveFramesAfterTerminalNoLock();
                accepted = true;
            }

            if (opusBytes.Length == 0)
                return accepted;

            if (_terminalSequence.HasValue &&
                IsSequenceAfter(sequence, _terminalSequence.Value))
                return false;

            int distance = ForwardDistance(_nextExpected, sequence);
            if (distance >= 0x8000 || distance > MaxFutureDistanceFrames)
                return false; // Late packet or unbounded future jump.

            if (_frames.ContainsKey(sequence))
                return accepted; // A duplicate may still carry an accepted End.

            if (_frames.Count >= MaxBufferedFrames)
                return false;

            _frames.Add(sequence, opusBytes);
            return true;
        }

        /// <summary>
        /// Returns at most one PCM frame per 20 ms playout tick.
        /// </summary>
        public JitterTickResult Tick()
        {
            lock (_sync)
            {
                if (_idle) return JitterTickResult.Empty;

                bool isFirst = false;
                if (!_started)
                {
                    if (_endOnlyAcquisition)
                        return FinishNoLock(pcm: null, isFirst: true);

                    if (HasPassedTerminalNoLock())
                        return FinishNoLock(pcm: null, isFirst: true);

                    int targetDepth = _playImmediately ? 1 : TargetDepthFrames;
                    bool completeShortStream = HasEveryFrameThroughTerminalNoLock();
                    if (_frames.Count < targetDepth && !completeShortStream)
                    {
                        if (!_terminalSequence.HasValue)
                            return JitterTickResult.Empty;

                        // Allow final voice datagrams to arrive after an early End.
                        _prebufferTicksAfterEnd++;
                        if (_prebufferTicksAfterEnd < targetDepth)
                            return JitterTickResult.Empty;
                    }

                    _started = true;
                    _playImmediately = false;
                    isFirst = true;
                }

                if (HasPassedTerminalNoLock())
                    return FinishNoLock(pcm: null, isFirst);

                ushort playedSequence = _nextExpected;
                if (_frames.Remove(playedSequence, out var opusBytes))
                {
                    _nextExpected++;
                    _concealedInARow = 0;

                    short[] pcm;
                    try
                    {
                        pcm = _opus.Decode(opusBytes);
                    }
                    catch
                    {
                        return AbortWithLifecycleNoLock();
                    }

                    RemoveLateFramesNoLock();
                    if (_terminalSequence == playedSequence)
                        return FinishNoLock(pcm, isFirst);

                    return new JitterTickResult
                    {
                        Pcm = pcm,
                        IsFirstFrame = isFirst,
                        IsLastFrame = false
                    };
                }

                if (_concealedInARow >= MaxConcealedInARow)
                    return FinishNoLock(pcm: null, isFirst);

                short[] concealed;
                try
                {
                    concealed = _opus.DecodePacketLoss();
                }
                catch
                {
                    return AbortWithLifecycleNoLock();
                }

                _concealedInARow++;
                _nextExpected++;
                bool isTerminal = _terminalSequence == playedSequence;
                RemoveLateFramesNoLock();

                if (isTerminal)
                    return FinishNoLock(concealed, isFirst);

                return new JitterTickResult
                {
                    Pcm = concealed,
                    IsFirstFrame = isFirst,
                    IsLastFrame = false
                };
            }
        }

        private void BeginStreamNoLock(ulong transmissionId, ushort firstSequence, bool playImmediately)
        {
            if (_hasActiveTransmission && _activeTransmissionId != transmissionId)
                RetireActiveTransmissionNoLock();

            _frames.Clear();
            _opus.ResetDecoder();
            _nextExpected = firstSequence;
            _terminalSequence = null;
            _activeTransmissionId = transmissionId;
            _hasActiveTransmission = true;
            _started = false;
            _idle = false;
            _playImmediately = playImmediately;
            _endOnlyAcquisition = false;
            _prebufferTicksAfterEnd = 0;
            _concealedInARow = 0;
        }

        private bool HasSameActiveEpochNoLock(ulong transmissionId) =>
            !_idle && _hasActiveTransmission && _activeTransmissionId == transmissionId;

        private void ResetPlaybackNoLock()
        {
            _frames.Clear();
            _opus.ResetDecoder();
            _nextExpected = 0;
            _terminalSequence = null;
            _activeTransmissionId = 0;
            _hasActiveTransmission = false;
            _started = false;
            _idle = true;
            _playImmediately = false;
            _endOnlyAcquisition = false;
            _prebufferTicksAfterEnd = 0;
            _concealedInARow = 0;
        }

        private void AbortNoLock()
        {
            RetireActiveTransmissionNoLock();
            ResetPlaybackNoLock();
        }

        private JitterTickResult AbortWithLifecycleNoLock()
        {
            // Close receive lifecycle when decoder failure resets the stream.
            AbortNoLock();
            return new JitterTickResult
            {
                Pcm = null,
                IsFirstFrame = false,
                IsLastFrame = true
            };
        }

        private JitterTickResult FinishNoLock(short[]? pcm, bool isFirst)
        {
            RetireActiveTransmissionNoLock();
            ResetPlaybackNoLock();
            return new JitterTickResult
            {
                Pcm = pcm,
                IsFirstFrame = isFirst,
                IsLastFrame = true
            };
        }

        private void RetireActiveTransmissionNoLock()
        {
            if (!_hasActiveTransmission || _activeTransmissionId == 0 ||
                _retiredTransmissionIds.Contains(_activeTransmissionId))
                return;

            _retiredTransmissionIds.Add(_activeTransmissionId);
            _retiredTransmissionOrder.Enqueue(_activeTransmissionId);
            while (_retiredTransmissionOrder.Count > MaxRetiredTransmissionIds)
                _retiredTransmissionIds.Remove(_retiredTransmissionOrder.Dequeue());
        }

        private bool HasPassedTerminalNoLock() =>
            _terminalSequence.HasValue && IsSequenceAfter(_nextExpected, _terminalSequence.Value);

        private bool HasEveryFrameThroughTerminalNoLock()
        {
            if (!_terminalSequence.HasValue) return false;

            int distance = ForwardDistance(_nextExpected, _terminalSequence.Value);
            if (distance >= 0x8000 || distance >= MaxBufferedFrames)
                return distance >= 0x8000; // Terminal is already behind playout.

            for (int i = 0; i <= distance; i++)
            {
                if (!_frames.ContainsKey(unchecked((ushort)(_nextExpected + i))))
                    return false;
            }

            return true;
        }

        private void RemoveFramesAfterTerminalNoLock()
        {
            if (!_terminalSequence.HasValue) return;

            var toRemove = new List<ushort>();
            foreach (ushort sequence in _frames.Keys)
            {
                if (IsSequenceAfter(sequence, _terminalSequence.Value))
                    toRemove.Add(sequence);
            }

            foreach (ushort sequence in toRemove)
                _frames.Remove(sequence);
        }

        private void RemoveLateFramesNoLock()
        {
            var toRemove = new List<ushort>();
            foreach (ushort sequence in _frames.Keys)
            {
                int distance = ForwardDistance(_nextExpected, sequence);
                if (distance >= 0x8000)
                    toRemove.Add(sequence);
            }

            foreach (ushort sequence in toRemove)
                _frames.Remove(sequence);
        }

        private static int ForwardDistance(ushort from, ushort to) =>
            unchecked((ushort)(to - from));

        private static bool IsSequenceAfter(ushort candidate, ushort reference)
        {
            int distance = ForwardDistance(reference, candidate);
            return distance != 0 && distance < 0x8000;
        }
    }
}
