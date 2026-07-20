using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RadioRelay.Shared.Protocol
{
    /// <summary>
    /// Reads and writes UTF-8 strings with a one-byte length prefix and safe
    /// 255-byte truncation.
    /// </summary>
    internal static class WireString
    {
        public static void Write(BinaryWriter w, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            if (bytes.Length > 255)
            {
                // Preserve complete UTF-8 scalars so authenticated names round-trip exactly.
                int validLength = 255;
                while (validLength > 0 && (bytes[validLength] & 0xc0) == 0x80)
                    validLength--;
                Array.Resize(ref bytes, validLength);
            }
            w.Write((byte)bytes.Length);
            w.Write(bytes);
        }

        public static string Read(BinaryReader r)
        {
            byte len = r.ReadByte();
            var bytes = r.ReadBytes(len);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// Carries one Opus frame or transmission control message.
    /// </summary>
    /// <remarks>
    /// The core wire format contains routing, sequence, encryption, identity,
    /// and payload fields. Optional trailing metadata adds the audio seed,
    /// transmission ID, start hint, and authenticated header tag.
    /// </remarks>
    public class AudioPacket
    {
        public Guid ClientId;
        public float Frequency;
        public ushort Sequence;
        public bool IsTransmissionStart;
        public bool IsTransmissionEnd;
        public bool IsEncrypted;

        /// <summary>
        /// Eight-byte passcode-derived routing identifier; all zeros means unencrypted.
        /// </summary>
        public byte[] NetIdHash = new byte[8];

        public byte[]? Nonce; // 12 bytes when encrypted.
        public byte[]? Tag;   // 16 bytes when encrypted.

        /// <summary>
        /// Sender callsign used for display and logging.
        /// </summary>
        public string SenderName = "";

        /// <summary>
        /// Source radio display name.
        /// </summary>
        public string RadioName = "";

        public byte[] Payload = Array.Empty<byte>();

        /// <summary>
        /// Per-transmission seed for hardware variation and receiver noise.
        /// </summary>
        public uint TransmissionAudioSeed;

        /// <summary>
        /// Nonzero per-PTT epoch; zero identifies packets without epoch metadata.
        /// </summary>
        public ulong TransmissionId;

        /// <summary>
        /// Redundant start marker used for packet-loss recovery.
        /// </summary>
        public bool IsTransmissionStartHint;

        /// <summary>
        /// Authenticates routing and lifecycle metadata on encrypted nets.
        /// </summary>
        public byte[]? HeaderAuthTag;

        /// <summary>
        /// Serializes the canonical bytes protected by <see cref="HeaderAuthTag"/>.
        /// </summary>
        public byte[] GetAuthenticatedHeaderBytes()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RadioRelay.AudioHeaderAuth.v1\0"));
            w.Write(ClientId.ToByteArray());
            w.Write(TransmissionId);
            w.Write(BitConverter.SingleToInt32Bits(Frequency));
            w.Write(Sequence);

            byte flags = 0;
            if (IsTransmissionStart) flags |= 0x1;
            if (IsTransmissionEnd) flags |= 0x2;
            if (IsEncrypted) flags |= 0x4;
            if (IsTransmissionStartHint) flags |= 0x8;
            w.Write(flags);

            var normalizedNetIdHash = new byte[8];
            if (NetIdHash != null)
                Array.Copy(NetIdHash, normalizedNetIdHash, Math.Min(8, NetIdHash.Length));
            w.Write(normalizedNetIdHash);
            WireString.Write(w, SenderName);
            WireString.Write(w, RadioName);
            w.Write(TransmissionAudioSeed);
            if (IsEncrypted)
            {
                var normalizedNonce = new byte[12];
                if (Nonce != null)
                    Array.Copy(Nonce, normalizedNonce, Math.Min(12, Nonce.Length));
                w.Write(normalizedNonce);

                var normalizedPayloadTag = new byte[16];
                if (Tag != null)
                    Array.Copy(Tag, normalizedPayloadTag, Math.Min(16, Tag.Length));
                w.Write(normalizedPayloadTag);
            }
            return ms.ToArray();
        }

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)PacketType.Audio);
            w.Write(ClientId.ToByteArray());
            w.Write(Frequency);
            w.Write(Sequence);

            byte flags = 0;
            if (IsTransmissionStart) flags |= 0x1;
            if (IsTransmissionEnd) flags |= 0x2;
            if (IsEncrypted) flags |= 0x4;
            w.Write(flags);

            w.Write(NetIdHash, 0, 8);

            if (IsEncrypted)
            {
                w.Write(Nonce ?? new byte[12], 0, 12);
                w.Write(Tag ?? new byte[16], 0, 16);
            }

            WireString.Write(w, SenderName);
            WireString.Write(w, RadioName);

            w.Write((ushort)Payload.Length);
            w.Write(Payload);

            if (HeaderAuthTag is { Length: 16 } || IsTransmissionStartHint)
            {
                w.Write((byte)3); // Audio metadata version.
                w.Write(TransmissionAudioSeed);
                w.Write(TransmissionId);
                byte extensionFlags = 0;
                if (HeaderAuthTag is { Length: 16 }) extensionFlags |= 0x1;
                if (IsTransmissionStartHint) extensionFlags |= 0x2;
                w.Write(extensionFlags);
                if (HeaderAuthTag is { Length: 16 })
                    w.Write(HeaderAuthTag, 0, 16);
            }
            else if (TransmissionId != 0)
            {
                w.Write((byte)2); // Audio metadata version.
                w.Write(TransmissionAudioSeed);
                w.Write(TransmissionId);
            }
            else if (TransmissionAudioSeed != 0)
            {
                w.Write((byte)1); // Audio metadata version.
                w.Write(TransmissionAudioSeed);
            }
            return ms.ToArray();
        }

        public static AudioPacket Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            r.ReadByte(); // Packet type was inspected by PacketPeek.
            var p = new AudioPacket
            {
                ClientId = new Guid(r.ReadBytes(16)),
                Frequency = r.ReadSingle(),
                Sequence = r.ReadUInt16()
            };

            byte flags = r.ReadByte();
            p.IsTransmissionStart = (flags & 0x1) != 0;
            p.IsTransmissionEnd = (flags & 0x2) != 0;
            p.IsEncrypted = (flags & 0x4) != 0;

            p.NetIdHash = r.ReadBytes(8);

            if (p.IsEncrypted)
            {
                p.Nonce = r.ReadBytes(12);
                p.Tag = r.ReadBytes(16);
            }

            p.SenderName = WireString.Read(r);
            p.RadioName = WireString.Read(r);

            ushort len = r.ReadUInt16();
            p.Payload = r.ReadBytes(len);
            if (ms.Position < ms.Length)
            {
                byte metadataVersion = r.ReadByte();
                if (metadataVersion >= 1 && ms.Length - ms.Position >= sizeof(uint))
                    p.TransmissionAudioSeed = r.ReadUInt32();
                if (metadataVersion >= 2 && ms.Length - ms.Position >= sizeof(ulong))
                    p.TransmissionId = r.ReadUInt64();
                if (metadataVersion >= 3 && ms.Position < ms.Length)
                {
                    byte extensionFlags = r.ReadByte();
                    if ((extensionFlags & 0x1) != 0 && ms.Length - ms.Position >= 16)
                        p.HeaderAuthTag = r.ReadBytes(16);
                    p.IsTransmissionStartHint = (extensionFlags & 0x2) != 0;
                }
            }
            return p;
        }
    }

    /// <summary>
    /// Advertises a client's frequency and net subscriptions to the server.
    /// </summary>
    public class SubscribePacket
    {
        public Guid ClientId;
        public float[] Frequencies = Array.Empty<float>();
        public PresenceSubscription[] Subscriptions = Array.Empty<PresenceSubscription>();

        /// <summary>
        /// Callsign used for presence and server logging.
        /// </summary>
        public string Callsign = "";

        /// <summary>
        /// Optional credential required to join the relay server.
        /// </summary>
        public string ServerPassword = "";

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)PacketType.Subscribe);
            w.Write(ClientId.ToByteArray());

            var frequencies = Frequencies.Length > 0
                ? Frequencies
                : Array.ConvertAll(Subscriptions, s => s.Frequency);
            w.Write((byte)Math.Min(255, frequencies.Length));
            foreach (var f in frequencies.Take(255)) w.Write(f);
            WireString.Write(w, Callsign);

            var subscriptions = Subscriptions.Length > 0
                ? Subscriptions
                : Array.ConvertAll(frequencies, f => new PresenceSubscription { Frequency = f, NetIdHash = new byte[8] });
            w.Write((byte)Math.Min(255, subscriptions.Length));
            foreach (var subscription in subscriptions.Take(255))
            {
                w.Write(subscription.Frequency);
                w.Write(PresenceSubscription.NormalizeHash(subscription.NetIdHash), 0, 8);
            }
            WireString.Write(w, ServerPassword);
            return ms.ToArray();
        }

        public static SubscribePacket Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            r.ReadByte();
            var p = new SubscribePacket { ClientId = new Guid(r.ReadBytes(16)) };
            byte count = r.ReadByte();
            p.Frequencies = new float[count];
            for (int i = 0; i < count; i++) p.Frequencies[i] = r.ReadSingle();
            // Accept packets that omit the optional callsign.
            p.Callsign = ms.Position < ms.Length ? WireString.Read(r) : "";

            if (ms.Position < ms.Length)
            {
                byte subscriptionCount = r.ReadByte();
                p.Subscriptions = new PresenceSubscription[subscriptionCount];
                for (int i = 0; i < subscriptionCount; i++)
                {
                    p.Subscriptions[i] = new PresenceSubscription
                    {
                        Frequency = r.ReadSingle(),
                        NetIdHash = r.ReadBytes(8)
                    };
                }
            }
            else
            {
                p.Subscriptions = Array.ConvertAll(p.Frequencies,
                    f => new PresenceSubscription { Frequency = f, NetIdHash = new byte[8] });
            }

            if (p.Frequencies.Length == 0 && p.Subscriptions.Length > 0)
                p.Frequencies = Array.ConvertAll(p.Subscriptions, s => s.Frequency);

            p.ServerPassword = ms.Position < ms.Length ? WireString.Read(r) : "";

            return p;
        }
    }

    /// <summary>
    /// Carries heartbeat and disconnect control messages.
    /// </summary>
    public class HeartbeatPacket
    {
        public Guid ClientId;
        public string ServerPassword = "";

        public byte[] Encode(PacketType type = PacketType.Heartbeat)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)type);
            w.Write(ClientId.ToByteArray());
            WireString.Write(w, ServerPassword);
            return ms.ToArray();
        }

        public static HeartbeatPacket Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            r.ReadByte();
            var p = new HeartbeatPacket { ClientId = new Guid(r.ReadBytes(16)) };
            p.ServerPassword = ms.Position < ms.Length ? WireString.Read(r) : "";
            return p;
        }
    }

    public static class PacketPeek
    {
        public static PacketType GetType(byte[] data) => (PacketType)data[0];
    }
}
