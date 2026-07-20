using System;
using Concentus.Enums;
using Concentus.Structs;

namespace RadioRelay.Shared.Audio
{
    /// <summary>
    /// Wraps Concentus Opus for 16 kHz mono, 20 ms voice frames.
    /// </summary>
    public class OpusCodec
    {
        public const int FrameSize = 320; // 20 ms at 16 kHz.

        private readonly int _sampleRate;
        private readonly int _bitrate;
        private OpusEncoder _encoder;
        private OpusDecoder _decoder;

        public OpusCodec(int sampleRate = 16000, int bitrate = 20000)
        {
            _sampleRate = sampleRate;
            _bitrate = bitrate;
            _encoder = CreateEncoder();
            _decoder = new OpusDecoder(sampleRate, 1);
        }

        /// <summary>
        /// Resets encoder prediction state for a new transmission.
        /// </summary>
        public void ResetEncoder() => _encoder = CreateEncoder();

        public void ResetDecoder() => _decoder = new OpusDecoder(_sampleRate, 1);

        private OpusEncoder CreateEncoder() =>
            new(_sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP)
            {
                Bitrate = _bitrate
            };

        public byte[] Encode(short[] pcmFrame)
        {
            var buffer = new byte[400]; // Upper bound for one 20 ms voice frame.
            int len = _encoder.Encode(pcmFrame, 0, FrameSize, buffer, 0, buffer.Length);
            var result = new byte[len];
            Array.Copy(buffer, result, len);
            return result;
        }

        public short[] Decode(byte[] opusBytes)
        {
            var pcm = new short[FrameSize];
            _decoder.Decode(opusBytes, 0, opusBytes.Length, pcm, 0, FrameSize, false);
            return pcm;
        }

        /// <summary>
        /// Uses Opus packet-loss concealment, falling back to silence on failure.
        /// </summary>
        public short[] DecodePacketLoss()
        {
            var pcm = new short[FrameSize];
            try
            {
                _decoder.Decode(null!, 0, 0, pcm, 0, FrameSize, false);
            }
            catch
            {
                Array.Clear(pcm, 0, pcm.Length); // Silence.
            }
            return pcm;
        }
    }
}
