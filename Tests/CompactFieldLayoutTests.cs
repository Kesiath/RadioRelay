using System.Reflection;
using System.Windows.Forms;
using RadioRelay.Client;
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
    public void Fixed_hud_swatch_is_centered_inside_its_value_cell()
    {
        using var swatch = new ModernButton { Text = string.Empty, Size = new Size(42, 24) };
        using var field = InvokeFieldFactory("CreateFixedField", "HUD", swatch, 50);

        field.Size = new Size(50, 44);
        field.CreateControl();
        field.PerformLayout();

        Assert.Equal(SizeType.Percent, field.ColumnStyles[0].SizeType);
        Assert.Equal(DockStyle.None, swatch.Dock);
        Assert.Equal(AnchorStyles.None, swatch.Anchor);
        Assert.Equal((field.ClientSize.Width - swatch.Width) / 2, swatch.Left);
        Assert.True(swatch.Top >= 16);
        Assert.True(swatch.Right <= field.DisplayRectangle.Right);
        Assert.True(swatch.Bottom <= field.DisplayRectangle.Bottom);
    }

    [Fact]
    public void Toolbar_actions_share_remaining_width_and_align_with_the_input_row()
    {
        var delay = new NumericTextBox();
        var lockButton = new ModernButton { Text = "Lock Controls" };
        var hudButton = new ModernButton { Text = "Customize HUD" };
        var exportButton = new ModernButton { Text = "Export Settings" };
        var importButton = new ModernButton { Text = "Import Settings" };
        var method = typeof(MainForm).GetMethod("CreateToolbarRows", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        using var row = Assert.IsType<TableLayoutPanel>(method!.Invoke(
            null,
            new object[] { delay, lockButton, hudButton, exportButton, importButton }));

        row.Size = new Size(724, 48);
        row.CreateControl();
        row.PerformLayout();

        Assert.Equal(9, row.ColumnCount);
        foreach (var column in new[] { 2, 4, 6, 8 })
            Assert.Equal(SizeType.Percent, row.ColumnStyles[column].SizeType);
        foreach (var button in new[] { lockButton, hudButton, exportButton, importButton })
        {
            Assert.Equal(16, button.Margin.Top);
            Assert.Equal(0, button.Margin.Bottom);
            Assert.Equal(32, button.Height);
        }
        Assert.InRange(Math.Abs(lockButton.Width - importButton.Width), 0, 2);
    }

    private static TableLayoutPanel InvokeFieldFactory(string methodName, string label, Control control, int width)
    {
        var method = typeof(MainForm).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<TableLayoutPanel>(method!.Invoke(null, new object[] { label, control, width }));
    }
}
