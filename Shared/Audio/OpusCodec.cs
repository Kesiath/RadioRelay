using System;
using Concentus.Enums;
using Concentus.Structs;

namespace RadioRelay.Shared.Audio
{
    /// 
    /// Thin wrapper around Concentus (pure C# Opus) fixed to RadioRelay's
    /// audio settings: 16 kHz mono, 20ms frames (320 samples/frame).
    /// Compresses ~256 kbit/s raw PCM down to roughly 16-24 kbit/s.
    /// 
    public class OpusCodec
    {
        public const int FrameSize = 320; // 20ms @ 16kHz

        private readonly int _sampleRate;
        private readonly OpusEncoder _encoder;
        private OpusDecoder _decoder;

        public OpusCodec(int sampleRate = 16000, int bitrate = 20000)
        {
            _sampleRate = sampleRate;
            _encoder = new OpusEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP)
            {
                Bitrate = bitrate
            };
            _decoder = new OpusDecoder(sampleRate, 1);
        }

        public void ResetDecoder() => _decoder = new OpusDecoder(_sampleRate, 1);

        public byte[] Encode(short[] pcmFrame)
        {
            var buffer = new byte[400]; // generous upper bound for a 20ms voice frame
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

        /// Asks Opus's built-in packet-loss concealment to synthesize
        /// a plausible frame in place of one that never arrived. Falls back
        /// to silence if the concealment call itself throws, so a frame
        /// that never showed up degrades to a gap rather than a crash.
        public short[] DecodePacketLoss()
        {
            var pcm = new short[FrameSize];
            try
            {
                _decoder.Decode(null!, 0, 0, pcm, 0, FrameSize, false);
            }
            catch
            {
                Array.Clear(pcm, 0, pcm.Length); // silence
            }
            return pcm;
        }
    }
}
