using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class PresenceProtocolTests
{
    private static readonly byte[] KeyA = { 1, 2, 3, 4, 5, 6, 7, 8 };
    private static readonly byte[] KeyB = { 8, 7, 6, 5, 4, 3, 2, 1 };

    [Fact]
    public void SubscribePacket_round_trips_frequency_key_subscriptions()
    {
        var id = Guid.NewGuid();
        var packet = new SubscribePacket
        {
            ClientId = id,
            Callsign = "Uzi 1",
            Subscriptions = new[]
            {
                new PresenceSubscription { Frequency = 251.000f, NetIdHash = KeyA },
                new PresenceSubscription { Frequency = 305.000f, NetIdHash = KeyB }
            }
        };

        var decoded = SubscribePacket.Decode(packet.Encode());

        Assert.Equal(id, decoded.ClientId);
        Assert.Equal("Uzi 1", decoded.Callsign);
        Assert.Equal(new[] { 251.000f, 305.000f }, decoded.Frequencies);
        Assert.Equal(2, decoded.Subscriptions.Length);
        Assert.Equal(251.000f, decoded.Subscriptions[0].Frequency);
        Assert.Equal(KeyA, decoded.Subscriptions[0].NetIdHash);
        Assert.Equal(305.000f, decoded.Subscriptions[1].Frequency);
        Assert.Equal(KeyB, decoded.Subscriptions[1].NetIdHash);
    }

    [Fact]
    public void SubscribePacket_round_trips_server_password()
    {
        var packet = new SubscribePacket
        {
            ClientId = Guid.NewGuid(),
            Callsign = "Uzi 1",
            Frequencies = new[] { 251.000f },
            ServerPassword = "swordfish"
        };

        var decoded = SubscribePacket.Decode(packet.Encode());

        Assert.Equal("swordfish", decoded.ServerPassword);
    }

    [Fact]
    public void HeartbeatPacket_round_trips_server_password()
    {
        var id = Guid.NewGuid();
        var packet = new HeartbeatPacket
        {
            ClientId = id,
            ServerPassword = "swordfish"
        };

        var decoded = HeartbeatPacket.Decode(packet.Encode());

        Assert.Equal(id, decoded.ClientId);
        Assert.Equal("swordfish", decoded.ServerPassword);
    }

    [Fact]
    public void PresenceUpdatePacket_round_trips_frequency_key_counts()
    {
        var twentyFourNames = Enumerable.Range(1, 24).Select(index => $"User {index:00}").ToArray();
        var packet = new PresenceUpdatePacket
        {
            TotalUserCount = twentyFourNames.Length,
            ConnectedClientNames = twentyFourNames,
            Counts = new[]
            {
                new PresenceChannelCount
                {
                    Frequency = 251.000f,
                    NetIdHash = KeyA,
                    UserCount = twentyFourNames.Length,
                    ClientNames = twentyFourNames
                },
                new PresenceChannelCount { Frequency = 251.000f, NetIdHash = KeyB, UserCount = 1 }
            }
        };

        var decoded = PresenceUpdatePacket.Decode(packet.Encode());

        Assert.Equal(PacketType.PresenceUpdate, PacketPeek.GetType(packet.Encode()));
        Assert.Equal(2, decoded.Counts.Length);
        Assert.Equal(251.000f, decoded.Counts[0].Frequency);
        Assert.Equal(KeyA, decoded.Counts[0].NetIdHash);
        Assert.Equal(24, decoded.Counts[0].UserCount);
        Assert.Equal(twentyFourNames, decoded.Counts[0].ClientNames);
        Assert.Equal(KeyB, decoded.Counts[1].NetIdHash);
        Assert.Equal(1, decoded.Counts[1].UserCount);
        Assert.Equal(24, decoded.TotalUserCount);
        Assert.Equal(twentyFourNames, decoded.ConnectedClientNames);
    }

    [Fact]
    public void Presence_counter_groups_by_frequency_and_key()
    {
        var counts = PresenceCounter.Build(new[]
        {
            new PresenceSubscription { Frequency = 251.000f, NetIdHash = KeyA },
            new PresenceSubscription { Frequency = 251.004f, NetIdHash = KeyA },
            new PresenceSubscription { Frequency = 251.000f, NetIdHash = KeyB },
            new PresenceSubscription { Frequency = 305.000f, NetIdHash = KeyA }
        });

        Assert.Equal(3, counts.Length);
        Assert.Contains(counts, c => c.Matches(251.000f, KeyA) && c.UserCount == 2);
        Assert.Contains(counts, c => c.Matches(251.000f, KeyB) && c.UserCount == 1);
        Assert.Contains(counts, c => c.Matches(305.000f, KeyA) && c.UserCount == 1);
    }
}
