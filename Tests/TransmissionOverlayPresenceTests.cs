using RadioRelay.Client.Radio;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class TransmissionOverlayPresenceTests
{
    private static Rectangle GetChipBoundsForTest(TransmissionOverlayForm overlay, RadioChannel channel, int index)
    {
        var method = typeof(TransmissionOverlayForm).GetMethod("GetChipBounds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TransmissionOverlayForm.GetChipBounds was not found.");
        return (Rectangle)method.Invoke(overlay, new object[] { channel, index })!;
    }

    [Fact]
    public void Overlay_is_twenty_percent_transparent_for_game_visibility()
    {
        var channel = new RadioChannel { Name = "RADIO 1", Frequency = 251.000f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });

        Assert.Equal(0.80, overlay.Opacity, precision: 2);
    }

    [Fact]
    public void Frequency_digit_count_does_not_change_hud_chip_width()
    {
        var channel = new RadioChannel { Name = "RADIO TEST LONG LABEL", Frequency = 9.000f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });

        overlay.ShowTransmission(channel, isLocalTransmit: true, remoteCallsign: "", localCallsign: "Uzi 1");
        var singleDigitFrequencyWidth = GetChipBoundsForTest(overlay, channel, 0).Width;

        channel.Frequency = 999.000f;
        var tripleDigitFrequencyWidth = GetChipBoundsForTest(overlay, channel, 0).Width;

        Assert.Equal(singleDigitFrequencyWidth, tripleDigitFrequencyWidth);
    }

    [Fact]
    public void Active_radio_chips_share_one_width_even_when_one_header_is_wider()
    {
        var channels = new List<RadioChannel>
        {
            new() { Name = "RADIO 1", Frequency = 251.000f },
            new() { Name = "RADIO 2", Frequency = 305.000f },
            new() { Name = "RADIO 3", Frequency = 100.000f }
        };
        using var overlay = new TransmissionOverlayForm(channels);

        overlay.SetUserCount(channels[0], 12);
        overlay.SetUserCount(channels[1], 1);
        overlay.SetUserCount(channels[2], 1);
        foreach (var channel in channels)
        {
            overlay.ShowTransmission(channel, isLocalTransmit: true, remoteCallsign: "", localCallsign: "Uzi 1");
        }

        var widths = channels
            .Select((channel, index) => GetChipBoundsForTest(overlay, channel, index).Width)
            .ToArray();

        Assert.Equal(widths[0], widths[1]);
        Assert.Equal(widths[0], widths[2]);
    }

    [Fact]
    public void User_count_survives_between_transmissions_until_presence_update_changes_it()
    {
        var channel = new RadioChannel { Name = "RADIO 1", Frequency = 251.000f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });

        overlay.SetUserCount(channel, 3);
        overlay.ShowTransmission(channel, isLocalTransmit: true, remoteCallsign: "", localCallsign: "Uzi 1");
        var firstHeader = overlay.GetHeadersForTest(channel).Single();
        overlay.HideTransmission(channel, isLocalTransmit: true);
        overlay.ShowTransmission(channel, isLocalTransmit: true, remoteCallsign: "", localCallsign: "Uzi 1");
        var secondHeader = overlay.GetHeadersForTest(channel).Single();

        Assert.Contains("3 users", firstHeader);
        Assert.Contains("3 users", secondHeader);
        Assert.DoesNotContain("0 users", secondHeader);
    }

    [Fact]
    public void Local_radio_name_is_used_in_transmission_header()
    {
        var channel = new RadioChannel { Name = "RADIO 1", LocalName = "Guard", Frequency = 251.000f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });

        overlay.ShowTransmission(channel, isLocalTransmit: true, remoteCallsign: "", localCallsign: "Uzi 1");

        var header = overlay.GetHeadersForTest(channel).Single();
        Assert.Contains("Guard", header);
        Assert.DoesNotContain("RADIO 1", header);
    }

    [Fact]
    public void Off_radio_suppresses_transmission_ui_but_preserves_user_count()
    {
        var channel = new RadioChannel { Name = "RADIO 1", Frequency = 251.000f, Volume = 1f };
        using var overlay = new TransmissionOverlayForm(new List<RadioChannel> { channel });
        overlay.SetUserCount(channel, 3);
        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Banshee", localCallsign: "", remoteClientId: "alpha");

        channel.Volume = 0f;
        overlay.SuppressChannel(channel);
        overlay.ShowTransmission(channel, isLocalTransmit: false, remoteCallsign: "Viper", localCallsign: "", remoteClientId: "bravo");

        Assert.Empty(overlay.GetHeadersForTest(channel));

        channel.Volume = 1f;
        overlay.SetEditMode(true);
        Assert.Contains("3 users", overlay.GetHeadersForTest(channel).Single());
    }
}
