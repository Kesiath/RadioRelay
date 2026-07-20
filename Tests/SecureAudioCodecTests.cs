using RadioRelay.Client.Networking;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;
using RadioRelay.Shared.Security;

namespace RadioRelay.Tests;

public class SecureAudioCodecTests
{
    [Fact]
    public void IndependentTransmitStreamsMatchIndependentOpusEncoders()
    {
        var codec = new SecureAudioCodec(16000);
        codec.BeginTransmitStream(101);
        codec.BeginTransmitStream(202);

        var expectedA = new OpusCodec(16000);
        var expectedB = new OpusCodec(16000);

        foreach (int frameNumber in Enumerable.Range(0, 8))
        {
            short[] frameA = ToneFrame(430, frameNumber);
            short[] frameB = ToneFrame(1170, frameNumber);

            byte[] encodedA = codec.EncodeAndEncrypt(frameA, NetOption.Unencrypted, 101).OpusPayload;
            byte[] encodedB = codec.EncodeAndEncrypt(frameB, NetOption.Unencrypted, 202).OpusPayload;

            Assert.Equal(expectedA.Encode(frameA), encodedA);
            Assert.Equal(expectedB.Encode(frameB), encodedB);
        }
    }

    [Fact]
    public void NewPttStartsWithFreshOpusPredictionHistory()
    {
        var codec = new SecureAudioCodec(16000);
        codec.BeginTransmitStream(1);
        for (int i = 0; i < 12; i++)
            codec.EncodeAndEncrypt(ToneFrame(2600, i), NetOption.Unencrypted, 1);
        Assert.True(codec.EndTransmitStream(1));

        codec.BeginTransmitStream(2);
        short[] firstFrame = ToneFrame(510, 0);

        byte[] actual = codec.EncodeAndEncrypt(firstFrame, NetOption.Unencrypted, 2).OpusPayload;
        byte[] expected = new OpusCodec(16000).Encode(firstFrame);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StreamLifecycleRejectsZeroDuplicateAndInactiveIds()
    {
        var codec = new SecureAudioCodec(16000);

        Assert.Throws<ArgumentOutOfRangeException>(() => codec.BeginTransmitStream(0));

        codec.BeginTransmitStream(77);
        Assert.Throws<InvalidOperationException>(() => codec.BeginTransmitStream(77));
        Assert.True(codec.EndTransmitStream(77));
        Assert.False(codec.EndTransmitStream(77));
        Assert.Throws<InvalidOperationException>(() =>
            codec.EncodeAndEncrypt(ToneFrame(500, 0), NetOption.Unencrypted, 77));
    }

    [Fact]
    public void EncryptedStreamsSharingANetNeverReuseNonces()
    {
        var codec = new SecureAudioCodec(16000);
        var net = NetOption.FromPasscode("nonce-test-passcode");
        codec.BeginTransmitStream(11);
        codec.BeginTransmitStream(12);
        var seen = new HashSet<string>();

        for (int i = 0; i < 64; i++)
        {
            ulong id = i % 2 == 0 ? 11UL : 12UL;
            var encoded = codec.EncodeAndEncrypt(ToneFrame(700 + i, i), net, id);
            Assert.NotNull(encoded.Nonce);
            Assert.True(SecureAudioCodec.HasModernHeaderNonce(encoded.Nonce));
            Assert.True(seen.Add(Convert.ToHexString(encoded.Nonce!)));
            Assert.Equal(
                encoded.OpusPayload,
                codec.DecryptToOpusFrame(encoded.Payload, encoded.Nonce, encoded.Tag, net.Key!));
        }
    }

    [Fact]
    public void OpusResetEncoderRestoresFreshEncoderState()
    {
        var codec = new OpusCodec(16000);
        for (int i = 0; i < 10; i++)
            codec.Encode(ToneFrame(2400, i));

        codec.ResetEncoder();
        short[] frame = ToneFrame(440, 0);

        Assert.Equal(new OpusCodec(16000).Encode(frame), codec.Encode(frame));
    }

    private static short[] ToneFrame(int frequency, int frameNumber)
    {
        var frame = new short[OpusCodec.FrameSize];
        int sampleOffset = frameNumber * frame.Length;
        for (int i = 0; i < frame.Length; i++)
        {
            double time = (sampleOffset + i) / 16000.0;
            frame[i] = (short)(Math.Sin(2 * Math.PI * frequency * time) * 12000);
        }

        return frame;
    }
}
