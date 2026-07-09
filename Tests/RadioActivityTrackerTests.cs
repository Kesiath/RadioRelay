using RadioRelay.Client.Radio;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class RadioActivityTrackerTests
{
    [Fact]
    public void Duplicate_remote_start_for_same_client_counts_once_and_end_returns_to_idle()
    {
        var tracker = new RadioActivityTracker();
        var channel = new RadioChannel { Name = "Radio 1", Frequency = 303.45f };
        var remote = Guid.NewGuid().ToString();

        tracker.RemoteStarted(channel, remote);
        tracker.RemoteStarted(channel, remote);

        Assert.True(tracker.IsReceiving(channel));

        tracker.RemoteEnded(channel, remote);

        Assert.False(tracker.IsReceiving(channel));
        Assert.Equal(RadioActivityKind.Idle, tracker.GetActivity(channel));
    }

    [Fact]
    public void Simultaneous_remote_ends_clear_receive_state_regardless_of_order()
    {
        var tracker = new RadioActivityTracker();
        var channel = new RadioChannel { Name = "Radio 1", Frequency = 303.45f };
        var alpha = Guid.NewGuid().ToString();
        var bravo = Guid.NewGuid().ToString();

        tracker.RemoteStarted(channel, alpha);
        tracker.RemoteStarted(channel, bravo);
        tracker.RemoteEnded(channel, bravo);
        tracker.RemoteEnded(channel, alpha);

        Assert.False(tracker.IsReceiving(channel));
        Assert.Equal(RadioActivityKind.Idle, tracker.GetActivity(channel));
    }

    [Fact]
    public void Blank_remote_end_defensively_clears_all_remote_receivers_for_channel()
    {
        var tracker = new RadioActivityTracker();
        var channel = new RadioChannel { Name = "Radio 1", Frequency = 303.45f };

        tracker.RemoteStarted(channel, Guid.NewGuid().ToString());
        tracker.RemoteStarted(channel, Guid.NewGuid().ToString());

        tracker.RemoteEnded(channel, "");

        Assert.False(tracker.IsReceiving(channel));
    }

    [Fact]
    public void Local_transmit_takes_visual_priority_over_receive_until_released()
    {
        var tracker = new RadioActivityTracker();
        var channel = new RadioChannel { Name = "Radio 1", Frequency = 303.45f };

        tracker.RemoteStarted(channel, Guid.NewGuid().ToString());
        tracker.LocalStarted(channel);

        Assert.Equal(RadioActivityKind.Transmitting, tracker.GetActivity(channel));

        tracker.LocalEnded(channel);

        Assert.Equal(RadioActivityKind.Receiving, tracker.GetActivity(channel));
    }

    [Fact]
    public void Late_remote_start_older_than_observed_end_is_ignored()
    {
        var tracker = new RadioActivityTracker();
        var channel = new RadioChannel { Name = "Radio 1", Frequency = 303.45f };
        var remote = Guid.NewGuid().ToString();

        // UI callbacks can be posted from different audio/network timer
        // threads. If the End callback reaches the UI first, a delayed older
        // Start must not resurrect an RX row after audio already stopped.
        tracker.RemoteEnded(channel, remote, lifecycleSequence: 2);
        tracker.RemoteStarted(channel, remote, lifecycleSequence: 1);

        Assert.False(tracker.IsReceiving(channel));
        Assert.Equal(RadioActivityKind.Idle, tracker.GetActivity(channel));

        tracker.RemoteStarted(channel, remote, lifecycleSequence: 3);

        Assert.True(tracker.IsReceiving(channel));
    }
}
