using System;
using System.Collections.Generic;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;
using RadioRelay.Shared.Security;

namespace RadioRelay.Client.Networking
{
    public class EncodedFrame
    {
        public byte[] Payload = Array.Empty<byte>(); // Opus bytes, or ciphertext when encrypted.
        // Pre-encryption Opus packet used by local receive-equivalent passthrough.
        public byte[] OpusPayload = Array.Empty<byte>();
        public byte[] NetIdHash = new byte[8];
        public bool IsEncrypted;
        public byte[]? Nonce;
        public byte[]? Tag;
    }

    /// <summary>
    /// Owns per-transmission Opus encoders and optional AES-GCM encryption.
    /// </summary>
    public class SecureAudioCodec
    {
        private sealed class TransmitEncoderState
        {
            public TransmitEncoderState(int sampleRate) => Codec = new OpusCodec(sampleRate);

            public OpusCodec Codec { get; }
            public object SyncRoot { get; } = new();
        }

        private readonly int _sampleRate;
        private readonly OpusCodec _legacyOpus;
        private readonly object _legacyEncodeLock = new();
        private readonly object _transmitStreamsLock = new();
        private readonly Dictionary<ulong, TransmitEncoderState> _transmitStreams = new();
        private readonly object _nonceGeneratorsLock = new();
        private readonly Dictionary<string, NonceGenerator> _nonceGenerators = new();

        public SecureAudioCodec(int sampleRate)
        {
            _sampleRate = sampleRate;
            _legacyOpus = new OpusCodec(sampleRate);
        }

        /// <summary>
        /// Creates isolated Opus state for one transmission ID.
        /// </summary>
        public void BeginTransmitStream(ulong transmissionId)
        {
            if (transmissionId == 0)
                throw new ArgumentOutOfRangeException(nameof(transmissionId), "Transmission ID zero is reserved for legacy packets.");

            lock (_transmitStreamsLock)
            {
                if (_transmitStreams.ContainsKey(transmissionId))
                    throw new InvalidOperationException($"Transmission {transmissionId} is already active.");

                _transmitStreams.Add(transmissionId, new TransmitEncoderState(_sampleRate));
            }
        }

        /// <summary>
        /// Releases a completed or abandoned transmission encoder.
        /// </summary>
        public bool EndTransmitStream(ulong transmissionId)
        {
            lock (_transmitStreamsLock)
                return _transmitStreams.Remove(transmissionId);
        }

        /// <summary>
        /// Drops all active transmission encoders.
        /// </summary>
        public void ClearTransmitStreams()
        {
            lock (_transmitStreamsLock)
                _transmitStreams.Clear();
        }

        /// <summary>
        /// Encodes one frame with isolated Opus state and optional encryption.
        /// </summary>
        public EncodedFrame EncodeAndEncrypt(short[] pcmFrame, NetOption net, ulong transmissionId)
        {
            TransmitEncoderState state;
            lock (_transmitStreamsLock)
            {
                if (!_transmitStreams.TryGetValue(transmissionId, out state!))
                    throw new InvalidOperationException(
                        $"Transmission {transmissionId} has not been started. Call BeginTransmitStream before encoding.");
            }

            byte[] opusBytes;
            lock (state.SyncRoot)
                opusBytes = state.Codec.Encode(pcmFrame);

            return EncryptOpusFrame(opusBytes, net);
        }

        /// <summary>
        /// Encodes through shared compatibility state when no transmission ID is available.
        /// </summary>
        public EncodedFrame EncodeAndEncrypt(short[] pcmFrame, NetOption net)
        {
            byte[] opusBytes;
            lock (_legacyEncodeLock)
                opusBytes = _legacyOpus.Encode(pcmFrame);

            return EncryptOpusFrame(opusBytes, net);
        }

        private EncodedFrame EncryptOpusFrame(byte[] opusBytes, NetOption net)
        {
            if (net.IsUnencrypted)
            {
                return new EncodedFrame
                {
                    Payload = opusBytes,
                    OpusPayload = opusBytes,
                    NetIdHash = new byte[8],
                    IsEncrypted = false
                };
            }

            string keyStr = Convert.ToBase64String(net.Key!);
            byte[] nonce;
            lock (_nonceGeneratorsLock)
            {
                if (!_nonceGenerators.TryGetValue(keyStr, out var gen))
                {
                    gen = new NonceGenerator();
                    _nonceGenerators[keyStr] = gen;
                }

                nonce = gen.Next();
                MarkModernHeaderNonce(nonce);
            }

            var (ciphertext, tag) = PacketCrypto.Encrypt(net.Key!, nonce, opusBytes);

            return new EncodedFrame
            {
                Payload = ciphertext,
                OpusPayload = opusBytes,
                NetIdHash = (byte[])net.NetIdHash.Clone(),
                IsEncrypted = true,
                Nonce = nonce,
                Tag = tag
            };
        }

        /// <summary>
        /// Decrypts an Opus payload, returning null when authentication fails.
        /// </summary>
        public byte[]? DecryptToOpusFrame(byte[] payload, byte[]? nonce, byte[]? tag, byte[] key)
        {
            if (nonce == null || tag == null) return null;
            return PacketCrypto.Decrypt(key, nonce, payload, tag);
        }

        /// <summary>
        /// Marks a nonce as requiring authenticated header metadata while
        /// preserving its random prefix and a 40-bit counter.
        /// </summary>
        internal static void MarkModernHeaderNonce(byte[] nonce)
        {
            if (nonce == null || nonce.Length != PacketCrypto.NonceSize)
                throw new ArgumentException("A modern audio nonce must be 12 bytes.", nameof(nonce));

            nonce[4] = (byte)(nonce[0] ^ 0x52);
            nonce[5] = (byte)(nonce[1] ^ 0x52);
            nonce[6] = (byte)(nonce[2] ^ 0x33);
        }

        internal static bool HasModernHeaderNonce(byte[]? nonce) =>
            nonce is { Length: PacketCrypto.NonceSize } &&
            nonce[4] == (byte)(nonce[0] ^ 0x52) &&
            nonce[5] == (byte)(nonce[1] ^ 0x52) &&
            nonce[6] == (byte)(nonce[2] ^ 0x33);

        internal static byte[] CreateModernControlNonce()
        {
            var nonce = new byte[PacketCrypto.NonceSize];
            System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
            MarkModernHeaderNonce(nonce);
            return nonce;
        }

        /// <summary>
        /// Encrypts an Opus frame with associated header data.
        /// </summary>
        public static (byte[] Ciphertext, byte[] Tag) EncryptOpusFrameWithAssociatedData(
            byte[] opusFrame,
            byte[] key,
            byte[] nonce,
            byte[] associatedData) =>
            PacketCrypto.Encrypt(key, nonce, opusFrame, associatedData);

        /// <summary>
        /// Decrypts an Opus frame with associated header data.
        /// </summary>
        public byte[]? DecryptToOpusFrame(
            byte[] payload,
            byte[]? nonce,
            byte[]? tag,
            byte[] key,
            byte[] associatedData)
        {
            if (nonce == null || tag == null) return null;
            return PacketCrypto.Decrypt(key, nonce, payload, tag, associatedData);
        }
    }
}
