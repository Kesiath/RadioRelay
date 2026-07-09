using System.Collections.Generic;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Client.AudioEngineNs
{
    /// Result of one JitterBuffer.Tick() call. IsFirstFrame/IsLastFrame
    /// tell the caller exactly when real playback of a transmission begins
    /// and ends, so start/end click sounds can be queued at the moment audio
    /// actually starts/finishes playing -- not when the network packet that
    /// announced start/end happened to arrive (which can be well ahead of or
    /// behind actual playout once jitter buffering and packet loss
    /// concealment are in the picture).
    public readonly struct JitterTickResult
    {
        public short[]? Pcm { get; init; }
        public bool IsFirstFrame { get; init; }
        public bool IsLastFrame { get; init; }

        public static readonly JitterTickResult Empty = new();
    }

    /// 
    /// Fixed-depth de-jitter buffer for one radio channel. Received (already
    /// decrypted) Opus frames are stored by sequence number; a steady 20ms
    /// ticker (driven externally by AudioEngine) pulls them out in order,
    /// using Opus's built-in packet-loss concealment to paper over any frame
    /// that hasn't arrived by the time it's due to play.
    ///
    /// Assumes one active transmitter per frequency at a time, same as a
    /// real analog radio -- if two people key up on the same frequency
    /// simultaneously they "walk over" each other in real radios too.
    /// 
    public class JitterBuffer
    {
        private const int TargetDepthFrames = 3; // ~60ms of buffering before playout starts
        private const int MaxConcealedInARow = 25; // ~500ms of concealment before giving up

        private readonly Dictionary<ushort, byte[]> _frames = new();
        private readonly OpusCodec _opus;
        private readonly object _sync = new();

        private ushort _nextExpected;
        private bool _started;
        private bool _ended;
        private bool _lastFired;
        private bool _idle = true; // true = fully drained, nothing to do until the next isStart
        private bool _playImmediately;
        private int _concealedInARow;

        public JitterBuffer(OpusCodec opus) => _opus = opus;

        public void Reset()
        {
            lock (_sync)
            {
                ResetNoLock();
            }
        }

        private void ResetNoLock()
        {
            _frames.Clear();
            _opus.ResetDecoder();
            _nextExpected = 0;
            _started = false;
            _ended = false;
            _lastFired = false;
            _idle = true;
            _playImmediately = false;
            _concealedInARow = 0;
        }

        public void OnFrameReceived(ushort sequence, byte[] opusBytes, bool isStart, bool isEnd, bool forceStart = false)
        {
            lock (_sync)
            {
                if (isStart || forceStart)
                {
                    _frames.Clear();
                    _opus.ResetDecoder();
                    _nextExpected = sequence;
                    _started = false;
                    _ended = false;
                    _lastFired = false;
                    _idle = false;
                    _playImmediately = forceStart;
                    _concealedInARow = 0;
                }

                if (opusBytes.Length > 0)
                    _frames[sequence] = opusBytes;

                if (isEnd) _ended = true;
            }
        }

        /// Call every ~20ms. Pcm is null if there is nothing to play
        /// on this tick (still pre-buffering, or the transmission has
        /// finished draining); IsFirstFrame/IsLastFrame mark the exact ticks
        /// where real playback begins/ends, even if Pcm itself is null on
        /// the tick where a same-tick empty transmission opens and closes.
        public JitterTickResult Tick()
        {
            lock (_sync)
            {
                // Once a transmission has fully finished draining, do nothing at
                // all on every subsequent tick until OnFrameReceived sees a fresh
                // isStart -- without this, "_ended && frames empty" would keep
                // re-entering the "not started yet" branch below forever (since
                // its guard requires !_ended to bail out), which re-fired
                // IsFirstFrame on literally every 20ms tick and caused the click
                // sound to loop endlessly after every received transmission.
                if (_idle) return JitterTickResult.Empty;

                bool isFirst = false;

                if (!_started)
                {
                    int targetDepth = _playImmediately ? 1 : TargetDepthFrames;
                    if (_frames.Count < targetDepth && !_ended) return JitterTickResult.Empty;
                    _started = true;
                    _playImmediately = false;
                    isFirst = true;
                }

                if (_frames.TryGetValue(_nextExpected, out var opusBytes))
                {
                    _frames.Remove(_nextExpected);
                    _nextExpected++;
                    _concealedInARow = 0;
                    short[] pcm;
                    try
                    {
                        pcm = _opus.Decode(opusBytes);
                    }
                    catch
                    {
                        ResetNoLock();
                        return JitterTickResult.Empty;
                    }

                    bool isLast = _ended && _frames.Count == 0;
                    if (isLast) { _started = false; _lastFired = true; _idle = true; }

                    return new JitterTickResult { Pcm = pcm, IsFirstFrame = isFirst, IsLastFrame = isLast };
                }

                if (_ended)
                {
                    _started = false;
                    // Nothing left to decode, but if we haven't yet signalled the
                    // end (e.g. a same-tick empty transmission, or the last real
                    // frame already drained on a previous tick without _ended
                    // being set yet), fire it now so the end click still plays.
                    bool isLast = !_lastFired;
                    _lastFired = true;
                    _idle = true;
                    return new JitterTickResult { Pcm = null, IsFirstFrame = isFirst, IsLastFrame = isLast };
                }

                if (_concealedInARow >= MaxConcealedInARow)
                {
                    // Sender's stream apparently died without an End flag; stop waiting.
                    _started = false;
                    bool isLast = !_lastFired;
                    _lastFired = true;
                    _idle = true;
                    return new JitterTickResult { Pcm = null, IsFirstFrame = isFirst, IsLastFrame = isLast };
                }

                _concealedInARow++;
                _nextExpected++;
                return new JitterTickResult { Pcm = _opus.DecodePacketLoss(), IsFirstFrame = isFirst, IsLastFrame = false };
            }
        }
    }
}
