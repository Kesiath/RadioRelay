using RadioRelay.Client.Radio;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class TransmissionOverlayMultiRxTests
{
    private static Rectangle GetChipBoundsForTest(TransmissionOverlayForm overlay, RadioChannel channel, int index)
    {
        var method = typeof(TransmissionOverlayForm).GetMethod("GetChipBounds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TransmissionOverlayForm.GetChipBounds was not found.");
        return (Rectangle)method.Invoke(overlay, new object[] { channel, index })!;
    }

    [Fact]
    public void Multiple_remote_rx_transmitters_are_displayed_together_and_end_independently()
    {
        var channel = new RadioChannel { Name = "RADIO 1", Frequency = 400.000f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });

        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Banshee", localCallsign: "Spectator", remoteClientId: "alpha");
        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Viper", localCallsign: "Spectator", remoteClientId: "bravo");
        overlay.ShowTransmission(channel, isLocalTransmit: true, remoteCallsign: "", localCallsign: "Spectator");

        var activeWhoLines = overlay.GetWhoLinesForTest(channel);
        Assert.Contains("Spectator", activeWhoLines);
        Assert.Contains(activeWhoLines, line => line.Contains("Banshee") && line.Contains("Viper"));

        overlay.HideTransmission(channel, isLocalTransmit: false, remoteClientId: "alpha");

        var afterAlphaEnds = overlay.GetWhoLinesForTest(channel);
        Assert.Contains("Spectator", afterAlphaEnds);
        Assert.Contains(afterAlphaEnds, line => line.Contains("Viper"));
        Assert.DoesNotContain(afterAlphaEnds, line => line.Contains("Banshee"));

        overlay.HideTransmission(channel, isLocalTransmit: false, remoteClientId: "bravo");

        var afterAllRxEnds = overlay.GetWhoLinesForTest(channel);
        Assert.Equal(new[] { "Spectator" }, afterAllRxEnds);
        Assert.DoesNotContain(overlay.GetHeadersForTest(channel), header => header.StartsWith("RX"));
    }

    [Fact]
    public void Long_remote_rx_names_stack_vertically_instead_of_expanding_one_line()
    {
        var channel = new RadioChannel { Name = "RADIO 1", Frequency = 400.000f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });
        var firstLongName = "Springfield Flight Lead With Extra Long Callsign";
        var secondLongName = "Enfield Package Commander With Extra Long Callsign";

        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: firstLongName, localCallsign: "Spectator", remoteClientId: "alpha");
        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: secondLongName, localCallsign: "Spectator", remoteClientId: "bravo");

        var activeWhoLines = overlay.GetWhoLinesForTest(channel);

        Assert.Contains(firstLongName, activeWhoLines);
        Assert.Contains(secondLongName, activeWhoLines);
        Assert.DoesNotContain(activeWhoLines, line => line.Contains(firstLongName) && line.Contains(secondLongName));
    }

    [Fact]
    public void Short_remote_rx_names_can_share_a_row_when_they_fit()
    {
        var channel = new RadioChannel { Name = "RADIO 1", Frequency = 400.000f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });

        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Bo", localCallsign: "Spectator", remoteClientId: "alpha");
        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Cy", localCallsign: "Spectator", remoteClientId: "bravo");

        Assert.Contains("Bo, Cy", overlay.GetWhoLinesForTest(channel));
    }

    [Fact]
    public void Many_short_remote_rx_names_wrap_to_new_rows_before_the_chip_expands()
    {
        var channel = new RadioChannel { Name = "RADIO 1", Frequency = 400.000f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });
        overlay.SetUserCount(channel, 6);

        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Banshee", localCallsign: "Spectator", remoteClientId: "alpha");
        var baselineWidth = GetChipBoundsForTest(overlay, channel, 0).Width;

        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Banshee", localCallsign: "Spectator", remoteClientId: "bravo");
        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Banshee", localCallsign: "Spectator", remoteClientId: "charlie");
        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Banshee", localCallsign: "Spectator", remoteClientId: "delta");
        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Banshee", localCallsign: "Spectator", remoteClientId: "echo");

        var whoLines = overlay.GetWhoLinesForTest(channel);

        Assert.True(whoLines.Count > 1);
        Assert.All(whoLines, line => Assert.Contains("Banshee", line));
        Assert.Equal(baselineWidth, GetChipBoundsForTest(overlay, channel, 0).Width);
    }

    [Fact]
    public void Edit_mode_preview_chip_uses_transmission_sized_bounds()
    {
        var channel = new RadioChannel { Name = "RADIO 1", Frequency = 400.000f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });

        overlay.SetEditMode(true);
        var editModeBounds = GetChipBoundsForTest(overlay, channel, 0);

        overlay.SetEditMode(false);
        overlay.ShowTransmission(channel, isLocalTransmit: true, remoteCallsign: "", localCallsign: "Preview");
        var transmissionBounds = GetChipBoundsForTest(overlay, channel, 0);

        Assert.Equal(transmissionBounds.Size, editModeBounds.Size);
    }

    [Fact]
    public void Late_remote_start_older_than_observed_end_does_not_resurrect_rx_chip()
    {
        var channel = new RadioChannel { Name = "RADIO 1", Frequency = 400.000f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });

        overlay.HideTransmission(channel, isLocalTransmit: false, remoteClientId: "alpha", lifecycleSequence: 2);
        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Banshee", localCallsign: "Spectator", remoteClientId: "alpha", lifecycleSequence: 1);

        Assert.DoesNotContain(overlay.GetHeadersForTest(channel), header => header.StartsWith("RX"));

        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Banshee", localCallsign: "Spectator", remoteClientId: "alpha", lifecycleSequence: 3);

        Assert.Contains(overlay.GetHeadersForTest(channel), header => header.StartsWith("RX"));
    }
}
