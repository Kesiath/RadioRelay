using RadioRelay.Client.AudioEngineNs;
using RadioRelay.Client.Networking;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;
using RadioRelay.Shared.Protocol;
using RadioRelay.Shared.Security;

namespace RadioRelay.Tests;

public class AudioIntegrationHardeningTests
{
    [Fact]
    public void Main_output_keeps_at_least_50ms_per_driver_buffer()
    {
        var factory = typeof(AudioEngine).GetMethod(
            "CreateMainWaveOut",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        using var output = Assert.IsType<NAudio.Wave.WaveOutEvent>(factory.Invoke(null, new object[] { -1 }));

        Assert.Equal(AudioEngine.MainOutputLatencyMilliseconds, output.DesiredLatency);
        Assert.Equal(AudioEngine.MainOutputBufferCount, output.NumberOfBuffers);
        Assert.True(output.DesiredLatency / output.NumberOfBuffers >= 50);
    }

    [Fact]
    public void Same_client_transmission_epochs_remain_independent_in_interference_tracking()
    {
        var clientId = Guid.NewGuid();
        var firstEpoch = new RadioTransmissionKey(clientId, 0x1001);
        var secondEpoch = new RadioTransmissionKey(clientId, 0x1002);
        var tracker = new RadioInterferenceTracker();

        Assert.True(tracker.ObserveTransmissionStart(firstEpoch).AcceptAudio);

        var overlap = tracker.ObserveTransmissionStart(secondEpoch);
        Assert.False(overlap.AcceptAudio);
        Assert.True(overlap.IsInterferingSender);
        Assert.True(tracker.HasInterference);

        var oldEnd = tracker.ObserveTransmissionEnd(firstEpoch);
        Assert.True(oldEnd.EndedPrimarySender);
        Assert.True(tracker.IsActive(secondEpoch));
        Assert.False(tracker.ShouldAcceptAudioFrom(secondEpoch));

        var reacquired = tracker.ObserveMidStreamTransmission(secondEpoch);
        Assert.True(reacquired.AcceptAudio);
        Assert.True(tracker.ShouldAcceptAudioFrom(secondEpoch));

        // A retired epoch's End must not close a newer epoch.
        Assert.False(tracker.ObserveTransmissionEnd(firstEpoch).EndedPrimarySender);
        Assert.True(tracker.ShouldAcceptAudioFrom(secondEpoch));
    }

    [Fact]
    public void Same_client_transmission_epochs_remain_independent_in_talkover_tracking()
    {
        var clientId = Guid.NewGuid();
        var firstEpoch = new RadioTransmissionKey(clientId, 0x2001);
        var secondEpoch = new RadioTransmissionKey(clientId, 0x2002);
        var monitor = new RadioTalkOverMonitor();

        monitor.SetLocalTransmitting(true);
        Assert.True(monitor.ObserveRemoteTransmissionStart(firstEpoch));
        Assert.False(monitor.ObserveRemoteTransmissionStart(secondEpoch));

        monitor.ObserveRemoteTransmissionEnd(firstEpoch);
        Assert.False(monitor.IsRemoteTransmitting(firstEpoch));
        Assert.True(monitor.IsRemoteTransmitting(secondEpoch));
        Assert.True(monitor.HasActiveOverlap);

        // Repeated controls remain scoped to their original epoch.
        monitor.ObserveRemoteTransmissionEnd(firstEpoch);
        Assert.True(monitor.IsRemoteTransmitting(secondEpoch));

        monitor.ObserveRemoteTransmissionEnd(secondEpoch);
        Assert.False(monitor.HasRemoteTransmitters);
        Assert.False(monitor.HasActiveOverlap);
    }

    [Fact]
    public void Modern_hud_identity_distinguishes_epochs_from_the_same_client()
    {
        var clientId = Guid.NewGuid();
        var first = new RadioTransmissionKey(clientId, 1);
        var second = new RadioTransmissionKey(clientId, 2);
        var legacy = new RadioTransmissionKey(clientId, 0);

        Assert.NotEqual(first.HudId, second.HudId);
        Assert.Equal(clientId.ToString(), legacy.HudId);
        Assert.Contains("0000000000000001", first.HudId);
    }

    [Fact]
    public void Redundant_encrypted_end_packets_preserve_latched_route_and_authenticate_every_copy()
    {
        var clientId = Guid.NewGuid();
        var channel = new RadioChannel
        {
            Name = "HF GUARD",
            Frequency = 7.250f,
            Passcode = "night-net"
        };
        var latchedFrequency = channel.Frequency;
        var latchedRadioName = channel.Name;
        var latchedNet = channel.SelectedNet;
        const ulong transmissionId = 0x1020304050607080;
        const uint audioSeed = 0xA1B2C3D4;

        var packets = AudioEngine.CreateTransmissionEndPackets(
            latchedFrequency,
            latchedRadioName,
            latchedNet,
            sequence: 91,
            callsign: "Banshee",
            clientId,
            transmissionId,
            audioSeed);

        // Retuning after key-up cannot rewrite the already-latched epoch.
        channel.Frequency = 399.975f;
        channel.Name = "UHF AUX";
        channel.Passcode = "different-net";

        Assert.Equal(3, packets.Count);
        Assert.Equal(3, packets.Distinct().Count());
        Assert.All(packets, packet =>
        {
            Assert.Equal(latchedFrequency, packet.Frequency);
            Assert.Equal(latchedRadioName, packet.RadioName);
            Assert.Equal(clientId, packet.ClientId);
            Assert.Equal(transmissionId, packet.TransmissionId);
            Assert.Equal(audioSeed, packet.TransmissionAudioSeed);
            Assert.Equal((ushort)91, packet.Sequence);
            Assert.True(packet.IsTransmissionEnd);
            Assert.False(packet.IsTransmissionStart);
            Assert.True(packet.IsEncrypted);
            Assert.Empty(packet.Payload);
            Assert.Equal(latchedNet.NetIdHash, packet.NetIdHash);
            Assert.NotNull(packet.HeaderAuthTag);
            Assert.True(PacketCrypto.VerifyHeaderAuthenticationTag(
                latchedNet.Key!,
                packet.GetAuthenticatedHeaderBytes(),
                packet.HeaderAuthTag));

            // The complete modern metadata extension must survive the wire.
            var decoded = AudioPacket.Decode(packet.Encode());
            Assert.Equal(transmissionId, decoded.TransmissionId);
            Assert.Equal(audioSeed, decoded.TransmissionAudioSeed);
            Assert.True(PacketCrypto.VerifyHeaderAuthenticationTag(
                latchedNet.Key!,
                decoded.GetAuthenticatedHeaderBytes(),
                decoded.HeaderAuthTag));
        });

        var tampered = AudioPacket.Decode(packets[0].Encode());
        tampered.Sequence++;
        Assert.False(PacketCrypto.VerifyHeaderAuthenticationTag(
            latchedNet.Key!,
            tampered.GetAuthenticatedHeaderBytes(),
            tampered.HeaderAuthTag));
    }

    [Fact]
    public void Input_gain_uses_a_symmetric_soft_knee_instead_of_hard_clipping()
    {
        float atKnee = AudioEngine.ApplyInputGainSoftClip(0.41f, 2f);
        float aboveKnee = AudioEngine.ApplyInputGainSoftClip(0.42f, 2f);
        float louder = AudioEngine.ApplyInputGainSoftClip(0.60f, 2f);
        float negative = AudioEngine.ApplyInputGainSoftClip(-0.60f, 2f);

        Assert.InRange(atKnee, 0.8199f, 0.8201f);
        Assert.InRange(aboveKnee, atKnee, 0.84f);
        Assert.InRange(louder, aboveKnee, 1f);
        Assert.Equal(louder, -negative, precision: 6);
        Assert.Equal(0f, AudioEngine.ApplyInputGainSoftClip(0.5f, -1f));

        // Above-scale values remain bounded without hard-clamp flattening.
        float overOne = AudioEngine.ApplyInputGainSoftClip(0.5f, 3f);
        float fartherOver = AudioEngine.ApplyInputGainSoftClip(0.75f, 3f);
        Assert.InRange(overOne, 0.82f, 1f);
        Assert.InRange(fartherOver, overOne, 1f);
        Assert.NotEqual(overOne, fartherOver);
    }

    [Fact]
    public void Passthrough_frame_uses_immutable_frequency_and_ear_snapshot_after_retune()
    {
        const float latchedFrequency = 25.000f;
        const uint seed = 0x13572468;
        const ulong transmissionId = 0xABCDEF;
        var channel = new RadioChannel
        {
            Frequency = latchedFrequency,
            Ear = RadioEar.Left
        };
        var opus = new OpusCodec(AudioEngine.SampleRate).Encode(ToneFrame());
        var frame = new LocalRadioPassthroughFrame(
            channel,
            opus,
            seed,
            transmissionId,
            latchedFrequency,
            IsIntercom: false,
            Ear: RadioEar.Left);

        // Model the UI mutating the live channel before the audio worker gets
        // around to this already-created frame.
        channel.Frequency = 400.000f;
        channel.Ear = RadioEar.Right;
        channel.IsIntercom = true;

        var actual = new LocalRadioPassthroughProcessor().Process(new[] { frame });

        var decoded = new OpusCodec(AudioEngine.SampleRate).Decode(opus);
        var expectedProfile = RadioEffectProfile.ForBand(
            RadioBandExtensions.FromFrequencyMHz(latchedFrequency),
            isIntercom: false,
            AudioEngine.SampleRate);
        expectedProfile.ResetReceive();
        var expectedNoise = new RadioNoiseGenerator(seed);
        var expectedMono = RadioReceiveFrameProcessor.Process(
            decoded,
            latchedFrequency,
            isIntercom: false,
            expectedProfile,
            expectedNoise);

        for (int i = 0; i < expectedMono.Length; i++)
        {
            short expectedLeft = (short)Math.Clamp(
                expectedMono[i] * 32767f,
                short.MinValue,
                short.MaxValue);
            Assert.Equal(expectedLeft, actual[i * 2]);
            Assert.Equal(0, actual[i * 2 + 1]);
        }
    }

    [Fact]
    public void Buffered_receive_handoff_preserves_its_end_and_gets_a_fresh_playout_window()
    {
        const ulong transmissionId = 0xB002;
        var remoteId = Guid.NewGuid();
        var sender = new OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new OpusCodec(AudioEngine.SampleRate));
        var rxState = new RxState
        {
            LastRemotePacketUtc = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(2))
        };
        var pending = new PendingReceiveHandoff
        {
            Transmission = new RadioTransmissionKey(remoteId, transmissionId),
            Callsign = "Viper",
            AudioSeed = 1234u,
            ReceiverOffsetMHz = 0.003f
        };
        rxState.PendingHandoffs.Add(pending);
        var nextPending = new PendingReceiveHandoff
        {
            Transmission = new RadioTransmissionKey(Guid.NewGuid(), transmissionId + 1),
            Callsign = "Raven",
            AudioSeed = 5678u,
            ReceiverOffsetMHz = -0.002f
        };
        var nextSender = new OpusCodec(AudioEngine.SampleRate);
        for (ushort sequence = 1; sequence <= 3; sequence++)
        {
            nextPending.Add(new PendingRemoteFrame(
                sequence,
                nextSender.Encode(ToneFrame()),
                IsStart: sequence == 1,
                IsEnd: false));
        }
        nextPending.Add(new PendingRemoteFrame(
            Sequence: 3,
            OpusPayload: Array.Empty<byte>(),
            IsStart: false,
            IsEnd: true));
        rxState.PendingHandoffs.Add(nextPending);

        for (ushort sequence = 1; sequence <= 3; sequence++)
        {
            pending.Add(new PendingRemoteFrame(
                sequence,
                sender.Encode(ToneFrame()),
                IsStart: sequence == 1,
                IsEnd: false));
        }
        pending.Add(new PendingRemoteFrame(
            Sequence: 3,
            OpusPayload: Array.Empty<byte>(),
            IsStart: false,
            IsEnd: true));

        DateTime activatedAfter = DateTime.UtcNow;
        AudioEngine.ActivatePendingReceiveHandoff(rxState, jitter);

        Assert.Equal(transmissionId, jitter.ActiveTransmissionId);
        Assert.True(rxState.HasAudibleReceiveInFlight);
        Assert.True(rxState.IsAudibleTransmissionTerminalPending);
        Assert.Equal(0.003f, rxState.PendingReceiverOffsetMHz);
        Assert.True(rxState.LastRemotePacketUtc >= activatedAfter);
        Assert.Single(rxState.PendingHandoffs);
        Assert.True(jitter.Tick().IsFirstFrame);
        Assert.False(jitter.Tick().IsLastFrame);
        Assert.True(jitter.Tick().IsLastFrame);

        AudioEngine.ActivatePendingReceiveHandoff(rxState, jitter);
        Assert.Equal(transmissionId + 1, jitter.ActiveTransmissionId);
        Assert.Empty(rxState.PendingHandoffs);
        Assert.True(rxState.IsAudibleTransmissionTerminalPending);
        Assert.Equal(-0.002f, rxState.PendingReceiverOffsetMHz);
    }

    [Fact]
    public void Capture_generation_boundary_does_not_drain_a_new_epoch_buffer_into_the_old_epoch()
    {
        using var engine = new AudioEngine(
            new List<RadioChannel> { new() { Frequency = 251f } },
            startAudioDevices: false);
        var engineType = typeof(AudioEngine);
        var advance = engineType.GetMethod(
            "AdvanceMicCaptureGeneration",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var drain = engineType.GetMethod(
            "DrainMicCaptureQueue",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var countField = engineType.GetField(
            "_micCaptureQueueCount",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var callback = engineType.GetMethod(
            "OnMicDataAvailable",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        long completedGeneration = (long)advance.Invoke(engine, null)!;
        callback.Invoke(engine, new object?[]
        {
            null,
            new NAudio.Wave.WaveInEventArgs(new byte[OpusCodec.FrameSize * 2], OpusCodec.FrameSize * 2)
        });
        drain.Invoke(engine, new object[] { completedGeneration });

        Assert.Equal(1, (int)countField.GetValue(engine)!);
        drain.Invoke(engine, new object[] { long.MaxValue });
        Assert.Equal(0, (int)countField.GetValue(engine)!);
    }

    [Fact]
    public void Stripping_modern_metadata_cannot_downgrade_an_encrypted_media_header()
    {
        const ulong transmissionId = 0xAABBCCDDEEFF0011;
        var net = NetOption.FromPasscode("authenticated-route");
        var codec = new SecureAudioCodec(AudioEngine.SampleRate);
        codec.BeginTransmitStream(transmissionId);
        var encoded = codec.EncodeAndEncrypt(ToneFrame(), net, transmissionId);
        var modern = new AudioPacket
        {
            ClientId = Guid.NewGuid(),
            Frequency = 251f,
            Sequence = 1,
            IsTransmissionStart = true,
            IsTransmissionStartHint = true,
            IsEncrypted = true,
            NetIdHash = encoded.NetIdHash,
            Nonce = encoded.Nonce,
            Tag = encoded.Tag,
            SenderName = "Banshee",
            RadioName = "Radio 1",
            TransmissionAudioSeed = 99u,
            TransmissionId = transmissionId,
            Payload = encoded.Payload
        };
        modern.HeaderAuthTag = PacketCrypto.ComputeHeaderAuthenticationTag(
            net.Key!, modern.GetAuthenticatedHeaderBytes());

        var legacyShape = new AudioPacket
        {
            ClientId = modern.ClientId,
            Frequency = modern.Frequency,
            Sequence = modern.Sequence,
            IsTransmissionStart = modern.IsTransmissionStart,
            IsEncrypted = true,
            NetIdHash = modern.NetIdHash,
            Nonce = modern.Nonce,
            Tag = modern.Tag,
            SenderName = modern.SenderName,
            RadioName = modern.RadioName,
            Payload = modern.Payload
        }.Encode();
        byte[] strippedWire = modern.Encode()[..legacyShape.Length];
        var stripped = AudioPacket.Decode(strippedWire);

        Assert.Equal(0UL, stripped.TransmissionId);
        Assert.Null(stripped.HeaderAuthTag);
        Assert.True(SecureAudioCodec.HasModernHeaderNonce(stripped.Nonce));
        Assert.True(AudioEngine.RequiresAuthenticatedHeader(stripped));

        stripped.Nonce![0] ^= 0x01;
        Assert.Null(codec.DecryptToOpusFrame(
            stripped.Payload,
            stripped.Nonce,
            stripped.Tag,
            net.Key!));
    }

    private static short[] ToneFrame()
    {
        var frame = new short[OpusCodec.FrameSize];
        for (int i = 0; i < frame.Length; i++)
            frame[i] = (short)(Math.Sin(i * 0.12) * 8_000);
        return frame;
    }
}
