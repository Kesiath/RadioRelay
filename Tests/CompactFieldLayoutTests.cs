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
    public void Fixed_hud_swatch_is_docked_inside_its_field_cell()
    {
        using var swatch = new ModernButton { Text = string.Empty };
        using var field = InvokeFieldFactory("CreateFixedField", "HUD", swatch, 50);

        field.Size = new Size(50, 44);
        field.CreateControl();
        field.PerformLayout();

        Assert.Equal(SizeType.Percent, field.ColumnStyles[0].SizeType);
        Assert.Equal(DockStyle.Fill, swatch.Dock);
        Assert.True(swatch.Right <= field.DisplayRectangle.Right);
        Assert.True(swatch.Bottom <= field.DisplayRectangle.Bottom);
    }

    private static TableLayoutPanel InvokeFieldFactory(string methodName, string label, Control control, int width)
    {
        var method = typeof(MainForm).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<TableLayoutPanel>(method!.Invoke(null, new object[] { label, control, width }));
    }
}
