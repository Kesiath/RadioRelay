using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class AudioPacketNoiseSeedTests
{
    [Fact]
    public void Audio_packet_round_trips_optional_receiver_noise_seed()
    {
        var encoded = new AudioPacket
        {
            ClientId = Guid.NewGuid(),
            Frequency = 251f,
            Sequence = 7,
            IsTransmissionStart = true,
            SenderName = "Viper",
            RadioName = "RADIO 1",
            Payload = new byte[] { 1, 2, 3, 4 },
            TransmissionAudioSeed = 0xA1B2C3D4u
        }.Encode();

        var decoded = AudioPacket.Decode(encoded);

        Assert.Equal(0xA1B2C3D4u, decoded.TransmissionAudioSeed);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, decoded.Payload);
    }

    [Fact]
    public void Legacy_audio_packet_without_metadata_remains_compatible()
    {
        var encoded = new AudioPacket
        {
            ClientId = Guid.NewGuid(),
            Frequency = 42.5f,
            Sequence = 3,
            Payload = new byte[] { 9, 8, 7 }
        }.Encode();

        var decoded = AudioPacket.Decode(encoded);

        Assert.Equal(0u, decoded.TransmissionAudioSeed);
        Assert.Equal(new byte[] { 9, 8, 7 }, decoded.Payload);
    }

    [Fact]
    public void Version_two_audio_metadata_round_trips_transmission_epoch_and_seed()
    {
        var encoded = new AudioPacket
        {
            ClientId = Guid.NewGuid(),
            Frequency = 251.125f,
            Sequence = 65535,
            IsTransmissionEnd = true,
            SenderName = "Viper",
            RadioName = "RADIO 2",
            Payload = Array.Empty<byte>(),
            TransmissionAudioSeed = 0x11223344u,
            TransmissionId = 0x1020304050607080ul
        }.Encode();

        var decoded = AudioPacket.Decode(encoded);

        Assert.Equal(0x11223344u, decoded.TransmissionAudioSeed);
        Assert.Equal(0x1020304050607080ul, decoded.TransmissionId);
        Assert.Equal((ushort)65535, decoded.Sequence);
        Assert.True(decoded.IsTransmissionEnd);
        Assert.Null(decoded.HeaderAuthTag);
    }

    [Fact]
    public void Version_three_audio_metadata_round_trips_header_authentication_tag()
    {
        var packet = new AudioPacket
        {
            ClientId = Guid.NewGuid(),
            Frequency = 42.625f,
            Sequence = 17,
            IsTransmissionStart = true,
            IsEncrypted = true,
            NetIdHash = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            Nonce = Enumerable.Range(20, 12).Select(i => (byte)i).ToArray(),
            Tag = Enumerable.Range(40, 16).Select(i => (byte)i).ToArray(),
            SenderName = "Banshee",
            RadioName = "RADIO 1",
            TransmissionAudioSeed = 0xAABBCCDDu,
            TransmissionId = 99,
            HeaderAuthTag = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            Payload = new byte[] { 3, 1, 4 }
        };

        var canonicalHeader = packet.GetAuthenticatedHeaderBytes();
        var decoded = AudioPacket.Decode(packet.Encode());

        Assert.Equal(packet.TransmissionId, decoded.TransmissionId);
        Assert.Equal(packet.HeaderAuthTag, decoded.HeaderAuthTag);
        Assert.Equal(canonicalHeader, decoded.GetAuthenticatedHeaderBytes());

        decoded.HeaderAuthTag![0] ^= 0xff;
        Assert.Equal(canonicalHeader, decoded.GetAuthenticatedHeaderBytes());
        decoded.Sequence++;
        Assert.NotEqual(canonicalHeader, decoded.GetAuthenticatedHeaderBytes());
        decoded.Sequence--;
        decoded.Nonce![0] ^= 0xff;
        Assert.NotEqual(canonicalHeader, decoded.GetAuthenticatedHeaderBytes());
        decoded.Nonce[0] ^= 0xff;
        decoded.Tag![0] ^= 0xff;
        Assert.NotEqual(canonicalHeader, decoded.GetAuthenticatedHeaderBytes());
    }

    [Fact]
    public void Version_three_audio_metadata_round_trips_redundant_start_hint()
    {
        var packet = new AudioPacket
        {
            ClientId = Guid.NewGuid(),
            Frequency = 251f,
            Sequence = 2,
            IsTransmissionStart = false,
            IsTransmissionStartHint = true,
            SenderName = "Banshee",
            RadioName = "RADIO 1",
            TransmissionAudioSeed = 44u,
            TransmissionId = 55u,
            Payload = new byte[] { 1, 2, 3 }
        };

        var decoded = AudioPacket.Decode(packet.Encode());

        Assert.False(decoded.IsTransmissionStart);
        Assert.True(decoded.IsTransmissionStartHint);
        Assert.Equal(packet.TransmissionId, decoded.TransmissionId);
        Assert.Equal(packet.GetAuthenticatedHeaderBytes(), decoded.GetAuthenticatedHeaderBytes());
    }

    [Fact]
    public void Authenticated_header_is_stable_when_unicode_name_hits_wire_limit()
    {
        var packet = new AudioPacket
        {
            ClientId = Guid.NewGuid(),
            Frequency = 251f,
            Sequence = 1,
            IsTransmissionStart = true,
            IsEncrypted = true,
            SenderName = string.Concat(Enumerable.Repeat("€", 100)),
            RadioName = "RADIO 1",
            TransmissionId = 123,
            HeaderAuthTag = new byte[16],
            Payload = new byte[] { 1 }
        };

        var canonicalBeforeEncoding = packet.GetAuthenticatedHeaderBytes();
        var decoded = AudioPacket.Decode(packet.Encode());

        Assert.Equal(canonicalBeforeEncoding, decoded.GetAuthenticatedHeaderBytes());
        Assert.DoesNotContain('�', decoded.SenderName);
    }
}
