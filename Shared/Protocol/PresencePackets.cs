using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RadioRelay.Shared.Protocol
{
    public readonly struct PresenceSubscription
    {
        public const float FrequencyTolerance = 0.005f;

        public float Frequency { get; init; }
        public byte[] NetIdHash { get; init; }

        public bool Matches(float frequency, byte[] netIdHash) =>
            Math.Abs(Frequency - frequency) <= FrequencyTolerance && NetIdHashesEqual(NetIdHash, netIdHash);

        internal static byte[] NormalizeHash(byte[]? hash)
        {
            var normalized = new byte[8];
            if (hash == null) return normalized;
            Array.Copy(hash, normalized, Math.Min(8, hash.Length));
            return normalized;
        }

        internal static bool NetIdHashesEqual(byte[]? a, byte[]? b)
        {
            var aa = NormalizeHash(a);
            var bb = NormalizeHash(b);
            for (int i = 0; i < 8; i++)
                if (aa[i] != bb[i]) return false;
            return true;
        }
    }

    public readonly struct PresenceChannelCount
    {
        public float Frequency { get; init; }
        public byte[] NetIdHash { get; init; }
        public int UserCount { get; init; }
        public string[] ClientNames { get; init; }

        public bool Matches(float frequency, byte[] netIdHash) =>
            Math.Abs(Frequency - frequency) <= PresenceSubscription.FrequencyTolerance &&
            PresenceSubscription.NetIdHashesEqual(NetIdHash, netIdHash);
    }

    public static class PresenceCounter
    {
        public static PresenceChannelCount[] Build(IEnumerable<PresenceSubscription> subscriptions)
        {
            var groups = new List<PresenceChannelCount>();

            foreach (var subscription in subscriptions)
            {
                int existingIndex = groups.FindIndex(g => g.Matches(subscription.Frequency, subscription.NetIdHash));
                if (existingIndex >= 0)
                {
                    var existing = groups[existingIndex];
                    groups[existingIndex] = new PresenceChannelCount
                    {
                        Frequency = existing.Frequency,
                        NetIdHash = existing.NetIdHash,
                        UserCount = existing.UserCount + 1
                    };
                }
                else
                {
                    groups.Add(new PresenceChannelCount
                    {
                        Frequency = subscription.Frequency,
                        NetIdHash = PresenceSubscription.NormalizeHash(subscription.NetIdHash),
                        UserCount = 1
                    });
                }
            }

            return groups.OrderBy(g => g.Frequency).ThenBy(g => Convert.ToHexString(g.NetIdHash)).ToArray();
        }
    }

    public class PresenceUpdatePacket
    {
        public PresenceChannelCount[] Counts = Array.Empty<PresenceChannelCount>();
        public int TotalUserCount;
        public string[] ConnectedClientNames = Array.Empty<string>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)PacketType.PresenceUpdate);
            w.Write((ushort)Counts.Length);
            foreach (var count in Counts)
            {
                w.Write(count.Frequency);
                w.Write(PresenceSubscription.NormalizeHash(count.NetIdHash), 0, 8);
                w.Write((ushort)Math.Clamp(count.UserCount, 0, ushort.MaxValue));
            }
            w.Write((ushort)Math.Clamp(TotalUserCount, 0, ushort.MaxValue));
            w.Write((ushort)Math.Clamp(ConnectedClientNames.Length, 0, ushort.MaxValue));
            foreach (var name in ConnectedClientNames.Take(ushort.MaxValue))
                WireString.Write(w, name);
            // Keep membership detail trailing so clients may stop after the total count.
            w.Write((ushort)Math.Clamp(Counts.Length, 0, ushort.MaxValue));
            foreach (var count in Counts.Take(ushort.MaxValue))
            {
                var names = count.ClientNames ?? Array.Empty<string>();
                w.Write((ushort)Math.Clamp(names.Length, 0, ushort.MaxValue));
                foreach (var name in names.Take(ushort.MaxValue)) WireString.Write(w, name);
            }
            return ms.ToArray();
        }

        public static PresenceUpdatePacket Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            r.ReadByte();
            ushort count = r.ReadUInt16();
            var packet = new PresenceUpdatePacket { Counts = new PresenceChannelCount[count] };
            for (int i = 0; i < count; i++)
            {
                packet.Counts[i] = new PresenceChannelCount
                {
                    Frequency = r.ReadSingle(),
                    NetIdHash = r.ReadBytes(8),
                    UserCount = r.ReadUInt16()
                };
            }
            if (ms.Position + sizeof(ushort) <= ms.Length)
                packet.TotalUserCount = r.ReadUInt16();
            if (ms.Position + sizeof(ushort) <= ms.Length)
            {
                ushort nameCount = r.ReadUInt16();
                var names = new List<string>(nameCount);
                for (int i = 0; i < nameCount && ms.Position < ms.Length; i++)
                    names.Add(WireString.Read(r));
                packet.ConnectedClientNames = names.ToArray();
            }
            if (ms.Position + sizeof(ushort) <= ms.Length)
            {
                ushort membershipCount = r.ReadUInt16();
                for (int i = 0; i < membershipCount && ms.Position + sizeof(ushort) <= ms.Length; i++)
                {
                    ushort nameCount = r.ReadUInt16();
                    var names = new List<string>(nameCount);
                    for (int j = 0; j < nameCount && ms.Position < ms.Length; j++)
                        names.Add(WireString.Read(r));

                    if (i < packet.Counts.Length)
                    {
                        var existing = packet.Counts[i];
                        packet.Counts[i] = new PresenceChannelCount
                        {
                            Frequency = existing.Frequency,
                            NetIdHash = existing.NetIdHash,
                            UserCount = existing.UserCount,
                            ClientNames = names.ToArray()
                        };
                    }
                }
            }
            return packet;
        }
    }
}
