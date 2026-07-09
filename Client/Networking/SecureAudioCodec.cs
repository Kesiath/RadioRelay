using System;
using System.Collections.Generic;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;
using RadioRelay.Shared.Security;

namespace RadioRelay.Client.Networking
{
    public class EncodedFrame
    {
        public byte[] Payload = Array.Empty<byte>(); // Opus bytes, ciphertext if Encrypted
        public byte[] NetIdHash = new byte[8];
        public bool IsEncrypted;
        public byte[]? Nonce;
        public byte[]? Tag;
    }

    /// 
    /// Sits between raw PCM and the wire: Opus-encodes/decodes, and applies
    /// AES-GCM encryption when a radio has a non-blank passcode set. One
    /// NonceGenerator is kept per distinct key so nonces are never reused
    /// under the same key even if multiple radios share a passcode.
    /// 
    public class SecureAudioCodec
    {
        private readonly OpusCodec _opus;
        private readonly Dictionary<string, NonceGenerator> _nonceGenerators = new();

        public SecureAudioCodec(int sampleRate) => _opus = new OpusCodec(sampleRate);

        public EncodedFrame EncodeAndEncrypt(short[] pcmFrame, NetOption net)
        {
            var opusBytes = _opus.Encode(pcmFrame);

            if (net.IsUnencrypted)
            {
                return new EncodedFrame { Payload = opusBytes, NetIdHash = new byte[8], IsEncrypted = false };
            }

            string keyStr = Convert.ToBase64String(net.Key!);
            if (!_nonceGenerators.TryGetValue(keyStr, out var gen))
            {
                gen = new NonceGenerator();
                _nonceGenerators[keyStr] = gen;
            }

            var nonce = gen.Next();
            var (ciphertext, tag) = PacketCrypto.Encrypt(net.Key!, nonce, opusBytes);

            return new EncodedFrame
            {
                Payload = ciphertext,
                NetIdHash = net.NetIdHash,
                IsEncrypted = true,
                Nonce = nonce,
                Tag = tag
            };
        }

        /// Decrypts a payload with a specific key derived fresh from
        /// whatever passcode the caller determined should apply *right now*
        /// -- there is deliberately no cache of "keys ever seen" here. That
        /// used to live in a dictionary populated whenever any passcode
        /// changed, and old entries never got removed, which meant traffic
        /// encrypted under an old passcode stayed decryptable forever even
        /// after the receiving radio's passcode field was changed to
        /// something else. Returns null if the packet fails AES-GCM
        /// authentication (tampered packet, or the key doesn't actually
        /// match despite the caller's NetIdHash pre-check).
        public byte[]? DecryptToOpusFrame(byte[] payload, byte[]? nonce, byte[]? tag, byte[] key)
        {
            if (nonce == null || tag == null) return null;
            return PacketCrypto.Decrypt(key, nonce, payload, tag);
        }
    }
}
