using System.Reflection;
using System.Windows.Forms;
using RadioRelay.Client;
using RadioRelay.Client.Radio;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class CompactFieldLayoutTests
{
    [Fact]
    public void Compact_field_uses_fixed_percent_column_instead_of_autosize_clipping()
    {
        using var editor = new TextBox();
        using var field = InvokeFieldFactory("CreateCompactField", "Server", editor, 144);

        field.Size = new Size(144, 44);
        field.CreateControl();
        field.PerformLayout();

        Assert.Single(field.ColumnStyles.Cast<ColumnStyle>());
        Assert.Equal(SizeType.Percent, field.ColumnStyles[0].SizeType);

        var host = field.GetControlFromPosition(0, 1);
        Assert.NotNull(host);
        Assert.True(host!.Right <= field.DisplayRectangle.Right);
        Assert.True(host.Bottom <= field.DisplayRectangle.Bottom);

        host.PerformLayout();
        Assert.Single(host.Controls.Cast<Control>());
        var child = host.Controls[0];
        Assert.True(child.Right <= host.ClientSize.Width - host.Padding.Right + 1);
    }

    [Fact]
    public void Fixed_hud_swatch_is_left_aligned_beneath_its_caption()
    {
        using var swatch = new ModernButton { Text = string.Empty, Size = new Size(42, 24) };
        using var field = InvokeFieldFactory("CreateFixedField", "HUD", swatch, 50);

        field.Size = new Size(50, 44);
        field.CreateControl();
        field.PerformLayout();

        Assert.Equal(SizeType.Percent, field.ColumnStyles[0].SizeType);
        Assert.Equal(DockStyle.None, swatch.Dock);
        Assert.Equal(AnchorStyles.Left, swatch.Anchor);
        Assert.Equal(0, swatch.Left);
        Assert.True(swatch.Top >= 16);
        Assert.True(swatch.Right <= field.DisplayRectangle.Right);
        Assert.True(swatch.Bottom <= field.DisplayRectangle.Bottom);
    }

    [Fact]
    public void Toolbar_actions_share_remaining_width_and_align_with_the_input_row()
    {
        var delay = new NumericTextBox();
        var icpButton = new ModernButton { Text = "ICP Unbound" };
        var lockButton = new ModernButton { Text = "Lock Controls" };
        var hudButton = new ModernButton { Text = "Customize HUD" };
        var exportButton = new ModernButton { Text = "Export Settings" };
        var importButton = new ModernButton { Text = "Import Settings" };
        var method = typeof(MainForm).GetMethod("CreateToolbarRows", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        using var row = Assert.IsType<TableLayoutPanel>(method!.Invoke(
            null,
            new object[] { delay, icpButton, lockButton, hudButton, exportButton, importButton }));

        row.Size = new Size(724, 48);
        row.CreateControl();
        row.PerformLayout();

        Assert.Equal(11, row.ColumnCount);
        foreach (var column in new[] { 2, 4, 6, 8, 10 })
            Assert.Equal(SizeType.Percent, row.ColumnStyles[column].SizeType);
        foreach (var button in new[] { icpButton, lockButton, hudButton, exportButton, importButton })
        {
            Assert.Equal(16, button.Margin.Top);
            Assert.Equal(0, button.Margin.Bottom);
            Assert.Equal(32, button.Height);
        }
        Assert.InRange(Math.Abs(lockButton.Width - importButton.Width), 0, 2);
    }

    [Fact]
    public void Server_user_total_is_inline_immediately_after_connect_button()
    {
        using var server = new TextBox();
        using var port = new NumericTextBox();
        using var password = new TextBox();
        using var connect = new ModernButton { Text = "Connect" };
        using var users = new Label { Text = "12 users" };
        using var status = new Label { Text = "Connected" };
        using var version = new Label { Text = "RadioRelay 1.6.0" };
        var method = typeof(MainForm).GetMethod("CreateServerRow", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        using var row = Assert.IsType<TableLayoutPanel>(method!.Invoke(
            null,
            new object[] { server, port, password, connect, users, status, version }));

        row.Size = new Size(724, 48);
        row.CreateControl();
        row.PerformLayout();

        Assert.True(users.Left > connect.Right);
        Assert.Equal(connect.Top, users.Top);
        Assert.Equal(connect.Height, users.Height);
    }

    [Fact]
    public void Radio_header_moves_metadata_left_and_distributes_it_evenly()
    {
        var method = typeof(MainForm).GetMethod("CreateRadioHeaderRow", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var titleBox = new TextBox
        {
            Text = "RADIO 1",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 8)
        };
        using var row = Assert.IsType<TableLayoutPanel>(method!.Invoke(
            null,
            new object[]
            {
                titleBox,
                new Label { Text = "0 users" },
                new TextBox(),
                new Label { Text = "OPEN" },
                new ModernButton { Size = new Size(42, 24) },
                new StatusBadge { Text = "IDLE", Size = new Size(58, 24), BackColor = Theme.SoftBorder }
            }));

        row.Size = new Size(710, 52);
        row.CreateControl();
        row.PerformLayout();

        var title = Assert.IsType<TextBox>(row.GetControlFromPosition(0, 0));
        Assert.Equal(new Padding(0, 10, 0, 8), title.Margin);
        Assert.Equal(SizeType.Absolute, row.ColumnStyles[0].SizeType);
        Assert.Equal((float)MainFormLayoutPolicy.RadioTitleColumnWidth, row.ColumnStyles[0].Width);
        foreach (var column in new[] { 2, 4, 6, 8 })
        {
            Assert.Equal(SizeType.Percent, row.ColumnStyles[column].SizeType);
            Assert.Equal(25f, row.ColumnStyles[column].Width);
        }
        Assert.Equal(SizeType.Absolute, row.ColumnStyles[10].SizeType);
        Assert.Equal((float)MainFormLayoutPolicy.RadioActivityBadgeColumnWidth, row.ColumnStyles[10].Width);
        var usersField = row.GetControlFromPosition(2, 0);
        Assert.NotNull(usersField);
        Assert.True(usersField!.Left < 200);
        var activityBadge = Assert.IsType<StatusBadge>(row.GetControlFromPosition(10, 0));
        Assert.Equal(MainFormLayoutPolicy.RadioActivityBadgeColumnWidth, activityBadge.Width);
        Assert.True(activityBadge.FlatRightEdge);
        Assert.Equal(Theme.SoftBorder, activityBadge.EffectiveBorderColor);
        Assert.Equal(AnchorStyles.Top | AnchorStyles.Right, activityBadge.Anchor);
        Assert.Equal(0, activityBadge.Top);
    }

    [Fact]
    public void Channel_selector_occupies_left_side_of_ptt_row_and_moves_ptt_a_right()
    {
        var channelBox = new DarkComboBox();
        var pttA = new ModernButton { Text = "PTT A" };
        var pttB = new ModernButton { Text = "PTT B" };
        var method = typeof(MainForm).GetMethod("CreatePttRow", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        using var row = Assert.IsType<TableLayoutPanel>(method!.Invoke(
            null,
            new object[] { channelBox, new Label(), pttA, new Label(), pttB }));

        row.Size = new Size(710, 46);
        row.CreateControl();
        row.PerformLayout();

        var channelField = row.GetControlFromPosition(0, 0);
        Assert.NotNull(channelField);
        Assert.True(channelField!.Left < pttA.Left);
        Assert.True(pttA.Left >= 100);
        Assert.Equal("PTT A", pttA.Text);
    }

    [Fact]
    public void Idle_badge_initializes_with_the_same_outline_color_as_its_rail()
    {
        var method = typeof(MainForm).GetMethod("CreateBadge", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        using var badge = Assert.IsType<StatusBadge>(method!.Invoke(
            null,
            new object[] { "IDLE", Theme.FaintText, Theme.SoftBorder }));
        badge.FlatRightEdge = true;

        Assert.Equal(Theme.SoftBorder, badge.BackColor);
        Assert.Equal(Theme.SoftBorder, badge.EffectiveBorderColor);
    }

    private static TableLayoutPanel InvokeFieldFactory(string methodName, string label, Control control, int width)
    {
        var method = typeof(MainForm).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<TableLayoutPanel>(method!.Invoke(null, new object[] { label, control, width }));
    }
}
