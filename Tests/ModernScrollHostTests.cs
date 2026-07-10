using System.Drawing;
using System.Windows.Forms;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class ModernScrollHostTests
{
    [Fact]
    public void Viewport_resize_requests_a_new_width_without_synchronously_relaying_out_content()
    {
        using var host = new ModernScrollHost { Size = new Size(820, 600) };
        using var content = new Panel { Size = new Size(760, 1200) };
        host.CreateControl();
        host.Content = content;
        host.PerformLayout();

        content.Width = host.ContentWidth;
        var completedWidth = content.Width;
        var widthChanges = 0;
        host.ContentWidthChanged += (_, _) => widthChanges++;

        host.Size = new Size(740, 600);
        host.PerformLayout();

        Assert.True(host.ContentWidth < completedWidth);
        Assert.Equal(completedWidth, content.Width);
        Assert.True(widthChanges > 0);
    }
}
