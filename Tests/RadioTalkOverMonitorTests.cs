using RadioRelay.Client.AudioEngineNs;

namespace RadioRelay.Tests;

public class RadioTalkOverMonitorTests
{
    [Fact]
    public void Remote_transmission_while_not_transmitting_does_not_warn_listener()
    {
        var monitor = new RadioTalkOverMonitor();

        bool shouldWarn = monitor.ObserveRemoteTransmissionStart(Guid.NewGuid());

        Assert.False(shouldWarn);
        Assert.False(monitor.IsLocalTransmitting);
        Assert.True(monitor.HasRemoteTransmitters);
    }

    [Fact]
    public void Remote_transmission_start_while_local_is_transmitting_warns_transmitter()
    {
        var monitor = new RadioTalkOverMonitor();
        monitor.SetLocalTransmitting(true);

        bool shouldWarn = monitor.ObserveRemoteTransmissionStart(Guid.NewGuid());

        Assert.True(shouldWarn);
    }

    [Fact]
    public void Local_transmit_start_while_remote_is_already_active_warns_transmitter()
    {
        var monitor = new RadioTalkOverMonitor();
        monitor.ObserveRemoteTransmissionStart(Guid.NewGuid());

        bool shouldWarn = monitor.SetLocalTransmitting(true);

        Assert.True(shouldWarn);
    }

    [Fact]
    public void Existing_overlap_warns_once_but_stays_active_until_overlap_clears()
    {
        var monitor = new RadioTalkOverMonitor();
        var remote = Guid.NewGuid();

        monitor.SetLocalTransmitting(true);
        Assert.True(monitor.ObserveRemoteTransmissionStart(remote));
        Assert.True(monitor.HasActiveOverlap);

        Assert.False(monitor.ObserveRemoteTransmissionStart(remote));
        Assert.False(monitor.SetLocalTransmitting(true));
        Assert.True(monitor.HasActiveOverlap);

        monitor.ObserveRemoteTransmissionEnd(remote);
        Assert.False(monitor.HasRemoteTransmitters);
        Assert.False(monitor.HasActiveOverlap);

        Assert.True(monitor.ObserveRemoteTransmissionStart(remote));
        Assert.True(monitor.HasActiveOverlap);
    }

    [Fact]
    public void Local_transmit_end_clears_warning_latch_but_keeps_remote_activity()
    {
        var monitor = new RadioTalkOverMonitor();
        var remote = Guid.NewGuid();

        monitor.SetLocalTransmitting(true);
        monitor.ObserveRemoteTransmissionStart(remote);

        monitor.SetLocalTransmitting(false);

        Assert.False(monitor.IsLocalTransmitting);
        Assert.True(monitor.HasRemoteTransmitters);
        Assert.True(monitor.IsRemoteTransmitting(remote));
        Assert.True(monitor.SetLocalTransmitting(true));
    }

    [Fact]
    public void Clear_remote_transmitters_stops_overlap_without_forgetting_local_transmit_state()
    {
        var monitor = new RadioTalkOverMonitor();
        monitor.SetLocalTransmitting(true);
        monitor.ObserveRemoteTransmissionStart(Guid.NewGuid());

        monitor.ClearRemoteTransmitters();

        Assert.True(monitor.IsLocalTransmitting);
        Assert.False(monitor.HasRemoteTransmitters);
        Assert.False(monitor.HasActiveOverlap);
        Assert.True(monitor.ObserveRemoteTransmissionStart(Guid.NewGuid()));
    }

    [Fact]
    public void Reset_clears_local_and_remote_overlap_state()
    {
        var monitor = new RadioTalkOverMonitor();
        monitor.SetLocalTransmitting(true);
        monitor.ObserveRemoteTransmissionStart(Guid.NewGuid());

        monitor.Reset();

        Assert.False(monitor.IsLocalTransmitting);
        Assert.False(monitor.HasRemoteTransmitters);
        Assert.False(monitor.SetLocalTransmitting(true));
    }
}
