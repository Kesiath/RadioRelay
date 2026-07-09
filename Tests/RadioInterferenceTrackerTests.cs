using RadioRelay.Client.AudioEngineNs;

namespace RadioRelay.Tests;

public class RadioInterferenceTrackerTests
{
    [Fact]
    public void First_audible_sender_is_captured_without_interference()
    {
        var tracker = new RadioInterferenceTracker();
        var primary = Guid.NewGuid();

        var decision = tracker.ObserveTransmissionStart(primary);

        Assert.True(decision.AcceptAudio);
        Assert.True(decision.IsPrimarySender);
        Assert.False(decision.IsInterferingSender);
        Assert.False(tracker.HasInterference);
    }

    [Fact]
    public void Overlapping_audible_sender_creates_interference_without_taking_over_capture()
    {
        var tracker = new RadioInterferenceTracker();
        var primary = Guid.NewGuid();
        var interferer = Guid.NewGuid();

        tracker.ObserveTransmissionStart(primary);
        var decision = tracker.ObserveTransmissionStart(interferer);

        Assert.False(decision.AcceptAudio);
        Assert.False(decision.IsPrimarySender);
        Assert.True(decision.IsInterferingSender);
        Assert.True(tracker.HasInterference);

        Assert.True(tracker.ShouldAcceptAudioFrom(primary));
        Assert.False(tracker.ShouldAcceptAudioFrom(interferer));
    }

    [Fact]
    public void Interference_clears_when_overlapping_sender_unkeys()
    {
        var tracker = new RadioInterferenceTracker();
        var primary = Guid.NewGuid();
        var interferer = Guid.NewGuid();

        tracker.ObserveTransmissionStart(primary);
        tracker.ObserveTransmissionStart(interferer);

        var decision = tracker.ObserveTransmissionEnd(interferer);

        Assert.False(decision.EndedPrimarySender);
        Assert.False(tracker.HasInterference);
        Assert.True(tracker.ShouldAcceptAudioFrom(primary));
    }

    [Fact]
    public void Capture_releases_when_primary_unkeys_even_if_interferer_is_still_keyed()
    {
        var tracker = new RadioInterferenceTracker();
        var primary = Guid.NewGuid();
        var interferer = Guid.NewGuid();

        tracker.ObserveTransmissionStart(primary);
        tracker.ObserveTransmissionStart(interferer);

        var decision = tracker.ObserveTransmissionEnd(primary);

        Assert.True(decision.EndedPrimarySender);
        Assert.False(tracker.HasPrimarySender);
        Assert.False(tracker.HasInterference);
        Assert.False(tracker.ShouldAcceptAudioFrom(primary));
        Assert.False(tracker.ShouldAcceptAudioFrom(interferer));
    }

    [Fact]
    public void Lone_remaining_sender_after_primary_unkeys_is_not_treated_as_active_interference()
    {
        var tracker = new RadioInterferenceTracker();
        var primary = Guid.NewGuid();
        var remaining = Guid.NewGuid();

        tracker.ObserveTransmissionStart(primary);
        tracker.ObserveTransmissionStart(remaining);
        tracker.ObserveTransmissionEnd(primary);

        Assert.False(tracker.HasPrimarySender);
        Assert.False(tracker.HasInterference);
    }

    [Fact]
    public void New_sender_can_be_captured_after_all_previous_senders_unkey()
    {
        var tracker = new RadioInterferenceTracker();
        var primary = Guid.NewGuid();
        var interferer = Guid.NewGuid();
        var next = Guid.NewGuid();

        tracker.ObserveTransmissionStart(primary);
        tracker.ObserveTransmissionStart(interferer);
        tracker.ObserveTransmissionEnd(primary);
        tracker.ObserveTransmissionEnd(interferer);

        var decision = tracker.ObserveTransmissionStart(next);

        Assert.True(decision.AcceptAudio);
        Assert.True(tracker.ShouldAcceptAudioFrom(next));
        Assert.False(tracker.HasInterference);
    }

    [Fact]
    public void Reset_clears_stale_capture_state_after_receiver_times_out()
    {
        var tracker = new RadioInterferenceTracker();
        var stalePrimary = Guid.NewGuid();
        var staleInterferer = Guid.NewGuid();
        var next = Guid.NewGuid();

        tracker.ObserveTransmissionStart(stalePrimary);
        tracker.ObserveTransmissionStart(staleInterferer);

        tracker.Reset();
        var decision = tracker.ObserveTransmissionStart(next);

        Assert.True(decision.AcceptAudio);
        Assert.True(tracker.ShouldAcceptAudioFrom(next));
        Assert.False(tracker.HasInterference);
    }

    [Fact]
    public void Mid_stream_sender_can_be_captured_after_local_transmit_mute_clears_receiver_state()
    {
        var tracker = new RadioInterferenceTracker();
        var remote = Guid.NewGuid();

        tracker.ObserveTransmissionStart(remote);
        tracker.Reset();

        var decision = tracker.ObserveMidStreamTransmission(remote);

        Assert.True(decision.AcceptAudio);
        Assert.True(decision.IsPrimarySender);
        Assert.True(tracker.ShouldAcceptAudioFrom(remote));
    }
}
