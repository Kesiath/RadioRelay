using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RadioRelay.Shared.Protocol
{
    /// 
    /// Shared helpers for the length-prefixed UTF8 strings used for callsigns
    /// and radio names on the wire. Capped at 255 bytes (encoded length fits
    /// in a single byte) and silently truncated if longer -- callsigns and
    /// radio names are short by nature, so this is never a practical limit.
    /// 
    internal static class WireString
    {
        public static void Write(BinaryWriter w, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            if (bytes.Length > 255) Array.Resize(ref bytes, 255);
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

    /// 
    /// A chunk of transmitted voice audio, tagged with the frequency it was
    /// sent on. The payload is an Opus frame -- plaintext if IsEncrypted is
    /// false, or AES-256-GCM ciphertext if true. The server only ever reads
    /// ClientId/Frequency/SenderName/RadioName for relay + logging purposes;
    /// it never needs to know about encryption at all.
    ///
    /// Wire layout:
    ///   byte    PacketType
    ///   16      ClientId (guid)
    ///   4       Frequency (float, MHz)
    ///   2       Sequence (ushort)
    ///   1       Flags (bit0=Start, bit1=End, bit2=Encrypted)
    ///   8       NetIdHash (all-zero if unencrypted)
    ///   [12     Nonce]   -- only present if Encrypted
    ///   [16     Tag]     -- only present if Encrypted
    ///   1+N     SenderName (length-prefixed UTF8, user-entered callsign)
    ///   1+N     RadioName (length-prefixed UTF8, e.g. "RADIO 1")
    ///   2       PayloadLength (ushort)
    ///   N       Payload (Opus frame, ciphertext if Encrypted)
    /// 
    public class AudioPacket
    {
        public Guid ClientId;
        public float Frequency;
        public ushort Sequence;
        public bool IsTransmissionStart;
        public bool IsTransmissionEnd;
        public bool IsEncrypted;

        /// 
        /// First 8 bytes of a hash derived from the transmitting radio's
        /// passcode. All-zero means "unencrypted / no passcode set". This is
        /// NOT the passcode itself and NOT the encryption key -- it only lets
        /// a receiver figure out *which* of their own known passcode-derived
        /// keys to try, without broadcasting the passcode on the wire.
        /// 
        public byte[] NetIdHash = new byte[8];

        public byte[]? Nonce; // 12 bytes, present only if IsEncrypted
        public byte[]? Tag;   // 16 bytes, present only if IsEncrypted

        /// The transmitting user's self-assigned callsign, so
        /// receivers (and the server's log) can show who's talking without
        /// any login/identity system.
        public string SenderName = "";

        /// The display name of the radio the transmission was sent
        /// on (e.g. "RADIO 1"), so receivers can show which radio in
        /// addition to the frequency.
        public string RadioName = "";

        public byte[] Payload = Array.Empty<byte>();

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
            return ms.ToArray();
        }

        public static AudioPacket Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            r.ReadByte(); // packet type -- already known by caller via PacketPeek
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
            return p;
        }
    }

    /// 
    /// Tells the server which frequencies a client currently wants to hear,
    /// along with the callsign they've entered. Sent on connect and whenever
    /// a radio's frequency changes. Passcodes/encryption are never sent to
    /// the server -- that stays a purely client-side, end-to-end concept.
    /// 
    public class SubscribePacket
    {
        public Guid ClientId;
        public float[] Frequencies = Array.Empty<float>();
        public PresenceSubscription[] Subscriptions = Array.Empty<PresenceSubscription>();

        /// User-entered callsign, used only for the server's
        /// connect/disconnect log lines -- never used for access control.
        public string Callsign = "";

        /// Optional server access password. This is separate from
        /// per-radio passcodes: it is used by relay administrators to decide
        /// whether a client may join this server at all.
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
            // Older callers always wrote a callsign, but guard defensively
            // in case the stream ends early (nothing else depends on this
            // succeeding at all costs; a missing callsign just shows blank).
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

    /// Simple keep-alive / liveness packet. Also reused for Disconnect (same shape).
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
