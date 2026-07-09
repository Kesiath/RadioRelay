using RadioRelay.Client.AudioEngineNs;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RemoteReceiveHudLifecycleTests
{
    [Fact]
    public void Local_transmit_end_uses_redundant_empty_end_packets_to_survive_udp_loss()
    {
        var channel = new RadioChannel { Name = "Radio 1", Frequency = 400.000f };

        var packets = AudioEngine.CreateTransmissionEndPackets(channel, sequence: 42, callsign: "Banshee");

        Assert.Equal(3, packets.Count);
        Assert.All(packets, packet =>
        {
            Assert.Equal(400.000f, packet.Frequency);
            Assert.Equal((ushort)42, packet.Sequence);
            Assert.False(packet.IsTransmissionStart);
            Assert.True(packet.IsTransmissionEnd);
            Assert.Equal("Banshee", packet.SenderName);
            Assert.Equal("Radio 1", packet.RadioName);
            Assert.Empty(packet.Payload);
        });
    }

    [Fact]
    public void Stale_rx_hud_fallback_clears_only_after_idle_timeout_with_no_local_or_audible_activity()
    {
        var lastPacket = new DateTime(2026, 7, 8, 20, 35, 0, DateTimeKind.Utc);
        var timeout = TimeSpan.FromMilliseconds(500);

        Assert.False(AudioEngine.ShouldFallbackClearStaleRemoteHud(
            isReceiveHudActive: true,
            localTransmitting: false,
            hasAudibleReceiveInFlight: false,
            isReceivingActive: false,
            lastRemotePacketUtc: lastPacket,
            nowUtc: lastPacket.AddMilliseconds(499),
            idleTimeout: timeout));

        Assert.True(AudioEngine.ShouldFallbackClearStaleRemoteHud(
            isReceiveHudActive: true,
            localTransmitting: false,
            hasAudibleReceiveInFlight: false,
            isReceivingActive: false,
            lastRemotePacketUtc: lastPacket,
            nowUtc: lastPacket.AddMilliseconds(500),
            idleTimeout: timeout));

        Assert.False(AudioEngine.ShouldFallbackClearStaleRemoteHud(
            isReceiveHudActive: true,
            localTransmitting: true,
            hasAudibleReceiveInFlight: false,
            isReceivingActive: false,
            lastRemotePacketUtc: lastPacket,
            nowUtc: lastPacket.AddMilliseconds(500),
            idleTimeout: timeout));

        Assert.True(AudioEngine.ShouldFallbackClearStaleRemoteHud(
            isReceiveHudActive: true,
            localTransmitting: false,
            hasAudibleReceiveInFlight: true,
            isReceivingActive: false,
            lastRemotePacketUtc: lastPacket,
            nowUtc: lastPacket.AddMilliseconds(500),
            idleTimeout: timeout));

        Assert.False(AudioEngine.ShouldFallbackClearStaleRemoteHud(
            isReceiveHudActive: true,
            localTransmitting: false,
            hasAudibleReceiveInFlight: true,
            isReceivingActive: true,
            lastRemotePacketUtc: lastPacket,
            nowUtc: lastPacket.AddMilliseconds(500),
            idleTimeout: timeout));
    }

    [Fact]
    public void Encrypted_end_control_packet_matches_channel_net_without_opus_payload()
    {
        var channel = new RadioChannel { Frequency = 400.000f, Passcode = "secret" };
        var packet = new AudioPacket
        {
            ClientId = Guid.NewGuid(),
            Frequency = 400.000f,
            IsTransmissionEnd = true,
            IsEncrypted = true,
            NetIdHash = channel.SelectedNet.NetIdHash,
            Payload = Array.Empty<byte>()
        };

        Assert.True(AudioEngine.PacketMatchesChannelForReceiveControl(channel, packet));
    }

    [Fact]
    public void End_control_for_displayed_remote_clears_rx_hud_even_when_local_tx_mutes_audio()
    {
        var channel = new RadioChannel { Frequency = 400.000f };
        var rxState = new RxState();
        var remoteId = Guid.NewGuid();
        var events = new List<string>();

        AudioEngine.ProcessRemoteReceiveHudControl(
            channel,
            rxState,
            new AudioPacket
            {
                ClientId = remoteId,
                Frequency = 400.000f,
                IsTransmissionStart = true,
                SenderName = "Banshee",
                Payload = Array.Empty<byte>()
            },
            start: (_, callsign) => events.Add($"start:{callsign}"),
            end: (_, callsign) => events.Add($"end:{callsign}"));

        AudioEngine.ProcessRemoteReceiveHudControl(
            channel,
            rxState,
            new AudioPacket
            {
                ClientId = remoteId,
                Frequency = 400.000f,
                IsTransmissionEnd = true,
                SenderName = "Banshee",
                Payload = Array.Empty<byte>()
            },
            start: (_, callsign) => events.Add($"start:{callsign}"),
            end: (_, callsign) => events.Add($"end:{callsign}"));

        Assert.Equal(new[] { "start:Banshee", "end:Banshee" }, events);
        Assert.False(rxState.IsReceiveHudActive);
    }

    [Fact]
    public void Accepted_remote_end_defers_hud_cleanup_until_rx_audio_drains_when_not_locally_transmitting()
    {
        Assert.True(AudioEngine.ShouldDeferRemoteHudEndUntilAudioDrains(
            wasAcceptedSender: true,
            localTransmitting: false,
            hasAudibleReceiveInFlight: true));
    }

    [Fact]
    public void Remote_end_does_not_defer_hud_cleanup_for_muted_rejected_or_never_audible_audio()
    {
        Assert.False(AudioEngine.ShouldDeferRemoteHudEndUntilAudioDrains(
            wasAcceptedSender: true,
            localTransmitting: true,
            hasAudibleReceiveInFlight: true));
        Assert.False(AudioEngine.ShouldDeferRemoteHudEndUntilAudioDrains(
            wasAcceptedSender: false,
            localTransmitting: false,
            hasAudibleReceiveInFlight: true));
        Assert.False(AudioEngine.ShouldDeferRemoteHudEndUntilAudioDrains(
            wasAcceptedSender: true,
            localTransmitting: false,
            hasAudibleReceiveInFlight: false));
    }

    [Fact]
    public void Mid_stream_reacquire_restores_pending_hud_identity_from_current_packet()
    {
        var rxState = new RxState();
        var remoteId = Guid.NewGuid();
        var packet = new AudioPacket
        {
            ClientId = remoteId,
            SenderName = "Banshee",
            IsTransmissionStart = false,
            IsTransmissionEnd = false
        };

        AudioEngine.ReacquireRemoteReceiveFromMidStream(rxState, packet);

        Assert.Equal(remoteId.ToString(), rxState.PendingRemoteClientId);
        Assert.Equal("Banshee", rxState.PendingRemoteCallsign);
    }

    [Fact]
    public void Mid_stream_reacquire_clears_stale_collided_receive_audio_before_new_sender_plays()
    {
        var rxState = new RxState();
        var remoteId = Guid.NewGuid();
        var receiveBuffer = new NAudio.Wave.BufferedWaveProvider(new NAudio.Wave.WaveFormat(AudioEngine.SampleRate, 16, 1));
        var staleCollidedAudio = new byte[AudioEngine.SampleRate / 10];
        receiveBuffer.AddSamples(staleCollidedAudio, 0, staleCollidedAudio.Length);

        AudioEngine.ReacquireRemoteReceiveFromMidStream(
            rxState,
            new AudioPacket
            {
                ClientId = remoteId,
                SenderName = "Banshee",
                IsTransmissionStart = false,
                IsTransmissionEnd = false
            },
            receiveBuffer);

        Assert.Equal(0, receiveBuffer.BufferedBytes);
        Assert.Equal(remoteId.ToString(), rxState.PendingRemoteClientId);
    }

    [Fact]
    public void Mid_stream_reacquire_resets_collision_effect_state_before_remaining_sender_resumes()
    {
        var rxState = new RxState();
        var freshCollision = new RadioCollisionDestructionModel(AudioEngine.SampleRate);
        var warmup = Enumerable.Range(0, 320).Select(i => MathF.Sin(i * 0.03f) * 0.45f).ToArray();
        rxState.CollisionDestruction.Process(warmup, active: true);

        AudioEngine.ReacquireRemoteReceiveFromMidStream(
            rxState,
            new AudioPacket
            {
                ClientId = Guid.NewGuid(),
                SenderName = "Banshee",
                IsTransmissionStart = false,
                IsTransmissionEnd = false
            });

        var reacquiredCollisionFrame = Enumerable.Range(0, 320).Select(i => MathF.Sin(i * 0.08f) * 0.5f).ToArray();
        var freshCollisionFrame = reacquiredCollisionFrame.ToArray();
        rxState.CollisionDestruction.Process(reacquiredCollisionFrame, active: true);
        freshCollision.Process(freshCollisionFrame, active: true);

        Assert.Equal(freshCollisionFrame, reacquiredCollisionFrame);
    }

    [Fact]
    public void Local_talkover_end_clears_queued_warning_audio_from_sidetone_path()
    {
        var rxState = new RxState();
        var sidetoneBuffer = new NAudio.Wave.BufferedWaveProvider(new NAudio.Wave.WaveFormat(AudioEngine.SampleRate, 16, 1));
        var queuedTalkoverWarning = new byte[AudioEngine.SampleRate / 10];
        sidetoneBuffer.AddSamples(queuedTalkoverWarning, 0, queuedTalkoverWarning.Length);

        AudioEngine.ClearLocalTalkOverWarning(rxState, sidetoneBuffer);

        Assert.Equal(0, sidetoneBuffer.BufferedBytes);
    }

    [Fact]
    public void Muted_remote_start_tracks_control_plane_without_capturing_audible_receiver()
    {
        var channel = new RadioChannel { Frequency = 400.000f };
        var rxState = new RxState();
        var remoteId = Guid.NewGuid();
        var events = new List<string>();

        rxState.TalkOver.SetLocalTransmitting(true);

        AudioEngine.ProcessMutedRemoteReceiveControl(
            channel,
            rxState,
            new AudioPacket
            {
                ClientId = remoteId,
                Frequency = 400.000f,
                IsTransmissionStart = true,
                SenderName = "Banshee"
            },
            start: (_, callsign, _) => events.Add($"start:{callsign}"),
            end: (_, callsign, _) => events.Add($"end:{callsign}"));

        rxState.TalkOver.SetLocalTransmitting(false);

        Assert.Equal(new[] { "start:Banshee" }, events);
        Assert.True(rxState.TalkOver.IsRemoteTransmitting(remoteId));
        Assert.False(rxState.Interference.HasPrimarySender);

        var decision = rxState.Interference.ObserveMidStreamTransmission(remoteId);
        Assert.True(decision.AcceptAudio);
    }

    [Fact]
    public void Drained_audio_reconciles_all_remaining_remote_hud_entries_when_no_remote_transmitters_remain()
    {
        var channel = new RadioChannel { Frequency = 303.450f };
        var rxState = new RxState();
        var alpha = Guid.NewGuid().ToString();
        var bravo = Guid.NewGuid().ToString();
        var ended = new List<string>();

        rxState.ActiveRemoteHudByClient[alpha] = "Banshee";
        rxState.ActiveRemoteHudByClient[bravo] = "Viper";

        AudioEngine.EndAllRemoteReceiveHuds(channel, rxState, (_, callsign, clientId) => ended.Add($"{callsign}:{clientId}"));

        Assert.False(rxState.IsReceiveHudActive);
        Assert.Contains($"Banshee:{alpha}", ended);
        Assert.Contains($"Viper:{bravo}", ended);
    }

    [Fact]
    public void Start_controls_track_multiple_remote_rx_owners_and_end_one_independently()
    {
        var channel = new RadioChannel { Frequency = 400.000f };
        var rxState = new RxState();
        var alpha = Guid.NewGuid();
        var bravo = Guid.NewGuid();
        var events = new List<string>();

        AudioEngine.ProcessRemoteReceiveHudControl(
            channel,
            rxState,
            new AudioPacket { ClientId = alpha, Frequency = 400.000f, IsTransmissionStart = true, SenderName = "Banshee" },
            start: (_, callsign) => events.Add($"start:{callsign}"),
            end: (_, callsign) => events.Add($"end:{callsign}"));
        AudioEngine.ProcessRemoteReceiveHudControl(
            channel,
            rxState,
            new AudioPacket { ClientId = bravo, Frequency = 400.000f, IsTransmissionStart = true, SenderName = "Viper" },
            start: (_, callsign) => events.Add($"start:{callsign}"),
            end: (_, callsign) => events.Add($"end:{callsign}"));
        AudioEngine.ProcessRemoteReceiveHudControl(
            channel,
            rxState,
            new AudioPacket { ClientId = alpha, Frequency = 400.000f, IsTransmissionEnd = true, SenderName = "Banshee" },
            start: (_, callsign) => events.Add($"start:{callsign}"),
            end: (_, callsign) => events.Add($"end:{callsign}"));

        Assert.Equal(new[] { "start:Banshee", "start:Viper", "end:Banshee" }, events);
        Assert.True(rxState.IsReceiveHudActive);
        Assert.Equal(bravo.ToString(), rxState.ActiveRemoteClientId);
    }

    [Fact]
    public void End_control_from_rejected_overlapping_sender_does_not_clear_current_rx_owner()
    {
        var channel = new RadioChannel { Frequency = 400.000f };
        var rxState = new RxState();
        var acceptedRemote = Guid.NewGuid();
        var rejectedRemote = Guid.NewGuid();
        var events = new List<string>();

        AudioEngine.ProcessRemoteReceiveHudControl(
            channel,
            rxState,
            new AudioPacket { ClientId = acceptedRemote, Frequency = 400.000f, IsTransmissionStart = true, SenderName = "Banshee" },
            start: (_, callsign) => events.Add($"start:{callsign}"),
            end: (_, callsign) => events.Add($"end:{callsign}"));

        AudioEngine.ProcessRemoteReceiveHudControl(
            channel,
            rxState,
            new AudioPacket { ClientId = rejectedRemote, Frequency = 400.000f, IsTransmissionEnd = true, SenderName = "Viper" },
            start: (_, callsign) => events.Add($"start:{callsign}"),
            end: (_, callsign) => events.Add($"end:{callsign}"));

        Assert.Equal(new[] { "start:Banshee" }, events);
        Assert.True(rxState.IsReceiveHudActive);
        Assert.Equal(acceptedRemote.ToString(), rxState.ActiveRemoteClientId);
    }
}
