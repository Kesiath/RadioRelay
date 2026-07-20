using System.Text;
using System.Security.Cryptography;
using RadioRelay.Shared.Protocol;
using RadioRelay.Shared.Security;

namespace RadioRelay.Tests;

public class PacketCryptoTests
{
    private static readonly byte[] Key = NetKeyDerivation.DeriveNetKey("crypto-test-passcode");

    [Fact]
    public void LegacyPayloadEncryptionStillRoundTripsWithoutAssociatedData()
    {
        byte[] nonce = Enumerable.Range(0, PacketCrypto.NonceSize).Select(i => (byte)i).ToArray();
        byte[] plaintext = Encoding.UTF8.GetBytes("legacy-compatible opus payload");

        var (ciphertext, tag) = PacketCrypto.Encrypt(Key, nonce, plaintext);

        Assert.Equal(plaintext, PacketCrypto.Decrypt(Key, nonce, ciphertext, tag));
        Assert.Null(PacketCrypto.Decrypt(Key, nonce, ciphertext, tag, Encoding.UTF8.GetBytes("unexpected aad")));
    }

    [Fact]
    public void LegacyPayloadTagBytesAreUnchangedByCompatibilityOverload()
    {
        byte[] nonce = Enumerable.Range(40, PacketCrypto.NonceSize).Select(i => (byte)i).ToArray();
        byte[] plaintext = Encoding.UTF8.GetBytes("old-client compatible payload");
        var expectedCiphertext = new byte[plaintext.Length];
        var expectedTag = new byte[PacketCrypto.TagSize];
        using (var aes = new AesGcm(Key, PacketCrypto.TagSize))
            aes.Encrypt(nonce, plaintext, expectedCiphertext, expectedTag);

        var actual = PacketCrypto.Encrypt(Key, nonce, plaintext);

        Assert.Equal(expectedCiphertext, actual.ciphertext);
        Assert.Equal(expectedTag, actual.tag);
    }

    [Fact]
    public void AssociatedDataIsAuthenticatedForFutureWireVersion()
    {
        byte[] nonce = Enumerable.Range(20, PacketCrypto.NonceSize).Select(i => (byte)i).ToArray();
        byte[] plaintext = Encoding.UTF8.GetBytes("opus payload");
        byte[] associatedData = Encoding.UTF8.GetBytes("canonical audio header");

        var (ciphertext, tag) = PacketCrypto.Encrypt(Key, nonce, plaintext, associatedData);

        Assert.Equal(plaintext, PacketCrypto.Decrypt(Key, nonce, ciphertext, tag, associatedData));

        byte[] tamperedHeader = (byte[])associatedData.Clone();
        tamperedHeader[0] ^= 0x40;
        Assert.Null(PacketCrypto.Decrypt(Key, nonce, ciphertext, tag, tamperedHeader));
        Assert.Null(PacketCrypto.Decrypt(Key, nonce, ciphertext, tag));
    }

    [Fact]
    public void HeaderAuthenticationTagRejectsMetadataTamperingAndWrongKey()
    {
        byte[] header = Encoding.UTF8.GetBytes("client|tx-id|frequency|sequence|flags|net|names|seed");
        byte[] tag = PacketCrypto.ComputeHeaderAuthenticationTag(Key, header);

        Assert.Equal(PacketCrypto.HeaderAuthenticationTagSize, tag.Length);
        Assert.True(PacketCrypto.VerifyHeaderAuthenticationTag(Key, header, tag));

        byte[] tamperedHeader = (byte[])header.Clone();
        tamperedHeader[^1] ^= 1;
        Assert.False(PacketCrypto.VerifyHeaderAuthenticationTag(Key, tamperedHeader, tag));

        byte[] otherKey = NetKeyDerivation.DeriveNetKey("different-passcode");
        Assert.False(PacketCrypto.VerifyHeaderAuthenticationTag(otherKey, header, tag));
        Assert.False(PacketCrypto.VerifyHeaderAuthenticationTag(Key, header, null));
        Assert.False(PacketCrypto.VerifyHeaderAuthenticationTag(Key, header, new byte[15]));
    }

    [Fact]
    public void HeaderAuthenticationUsesAKeyDomainSeparateFromPayloadGcm()
    {
        byte[] nonce = Enumerable.Repeat((byte)0xA5, PacketCrypto.NonceSize).ToArray();
        byte[] header = Encoding.UTF8.GetBytes("same bytes");
        var (_, payloadTag) = PacketCrypto.Encrypt(Key, nonce, header);
        byte[] headerTag = PacketCrypto.ComputeHeaderAuthenticationTag(Key, header);

        Assert.NotEqual(payloadTag, headerTag);
    }

    [Fact]
    public void EncryptedEmptyEndPacketRetainsAndVerifiesHeaderAuthenticationTag()
    {
        var packet = new AudioPacket
        {
            ClientId = Guid.NewGuid(),
            Frequency = 251.725f,
            Sequence = 412,
            IsTransmissionEnd = true,
            IsEncrypted = true,
            NetIdHash = NetKeyDerivation.ComputeNetIdHash("crypto-test-passcode"),
            Nonce = new byte[PacketCrypto.NonceSize],
            Tag = new byte[PacketCrypto.TagSize],
            SenderName = "VIPER",
            RadioName = "RADIO 2",
            TransmissionAudioSeed = 0x10203040,
            TransmissionId = 0x0102030405060708,
            Payload = Array.Empty<byte>()
        };
        packet.HeaderAuthTag = PacketCrypto.ComputeHeaderAuthenticationTag(
            Key,
            packet.GetAuthenticatedHeaderBytes());

        AudioPacket decoded = AudioPacket.Decode(packet.Encode());

        Assert.Empty(decoded.Payload);
        Assert.Equal(packet.TransmissionId, decoded.TransmissionId);
        Assert.True(PacketCrypto.VerifyHeaderAuthenticationTag(
            Key,
            decoded.GetAuthenticatedHeaderBytes(),
            decoded.HeaderAuthTag));

        decoded.Sequence++;
        Assert.False(PacketCrypto.VerifyHeaderAuthenticationTag(
            Key,
            decoded.GetAuthenticatedHeaderBytes(),
            decoded.HeaderAuthTag));
    }

    [Fact]
    public void MalformedNetworkCryptoFieldsFailClosedWithoutThrowing()
    {
        byte[] ciphertext = [1, 2, 3];

        Assert.Null(PacketCrypto.Decrypt(Key, new byte[11], ciphertext, new byte[16]));
        Assert.Null(PacketCrypto.Decrypt(Key, new byte[12], ciphertext, new byte[15]));
        Assert.Null(PacketCrypto.Decrypt(new byte[4], new byte[12], ciphertext, new byte[16]));
    }
}
