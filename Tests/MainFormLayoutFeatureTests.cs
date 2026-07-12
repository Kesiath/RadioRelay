using RadioRelay.Client;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class MainFormLayoutFeatureTests
{
    [Fact]
    public void Radio_cards_budget_room_for_readable_vertical_controls()
    {
        Assert.InRange(MainFormLayoutPolicy.RadioCardHeight, 168, 188);
    }

    [Fact]
    public void Setup_strip_keeps_text_fields_from_clipping()
    {
        Assert.InRange(MainFormLayoutPolicy.SetupStripHeight, 126, 150);
    }

    [Fact]
    public void Fixed_window_width_supports_the_modern_three_column_device_layout()
    {
        Assert.Equal(820, MainFormLayoutPolicy.FixedWindowWidth);
        Assert.Equal(760, MainFormLayoutPolicy.MaxContentWidth);
        Assert.True(MainFormLayoutPolicy.FixedWindowWidth > MainFormLayoutPolicy.MaxContentWidth);
    }

    [Fact]
    public void Radio_header_reserves_breathing_room_for_activity_badge()
    {
        Assert.InRange(MainFormLayoutPolicy.RadioTitleColumnWidth, 128, 152);
        Assert.InRange(MainFormLayoutPolicy.RadioActivityBadgeColumnWidth, 64, 80);
    }

    [Fact]
    public void Activity_badge_fills_its_dedicated_status_column()
    {
        Assert.Equal(70, MainFormLayoutPolicy.RadioActivityBadgeWidth);
        Assert.Equal(MainFormLayoutPolicy.RadioActivityBadgeColumnWidth, MainFormLayoutPolicy.RadioActivityBadgeWidth);
    }

    [Fact]
    public void Radio_title_font_is_larger_than_top_wordmark_title_font()
    {
        Assert.True(Theme.RadioTitleFont.Size > Theme.TitleFont.Size);
        Assert.InRange(Theme.RadioTitleFont.Size, 16f, 18f);
    }

    [Fact]
    public void Long_radio_names_scale_down_without_changing_short_names()
    {
        var shortNameSize = MainForm.GetRadioNameFontSize("RADIO 1", MainFormLayoutPolicy.RadioTitleColumnWidth);
        var longNameSize = MainForm.GetRadioNameFontSize("VERY LONG RADIO CHANNEL", MainFormLayoutPolicy.RadioTitleColumnWidth);

        Assert.Equal(Theme.RadioTitleFont.Size, shortNameSize);
        Assert.InRange(longNameSize, 4f, Theme.RadioTitleFont.Size - 0.1f);
    }

    [Fact]
    public void Radio_name_fit_reserves_space_for_textbox_inset_and_caret()
    {
        const int width = MainFormLayoutPolicy.RadioTitleColumnWidth;
        var size = MainForm.GetRadioNameFontSize("thisisaverybigna", width);
        using var font = new Font(Theme.RadioTitleFont.FontFamily, size, Theme.RadioTitleFont.Style);

        var renderedWidth = TextRenderer.MeasureText(
            "thisisaverybigna",
            font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

        Assert.True(renderedWidth <= width - 14);
    }

    [Fact]
    public void Compact_header_text_is_brighter_than_inactive_text()
    {
        Assert.True(Theme.HeaderText.GetBrightness() > Theme.FaintText.GetBrightness());
    }
}
