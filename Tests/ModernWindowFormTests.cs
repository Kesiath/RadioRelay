using System.Drawing;
using System.Threading;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class ModernWindowFormTests
{
    private sealed class TestWindow : ModernWindowForm
    {
        public Control ExposedContentHost => ContentHost;
        public ModernTitleBar ExposedTitleBar => TitleBar;
    }

    [Fact]
    public void Modern_window_uses_custom_chrome_and_retains_standard_window_actions()
    {
        using var window = new TestWindow();

        Assert.Equal(FormBorderStyle.None, window.FormBorderStyle);
        Assert.True(window.MinimizeBox);
        Assert.True(window.MaximizeBox);
        Assert.NotNull(window.ExposedContentHost);

        window.Size = new Size(800, 600);
        window.PerformLayout();

        Assert.Equal(window.ExposedTitleBar.Bottom, window.ExposedContentHost.Top);
        Assert.Equal(window.ClientSize.Height - window.Padding.Bottom, window.ExposedContentHost.Bottom);
    }

    [Fact]
    public void Fixed_width_constraints_still_allow_vertical_resizing_and_hide_maximize()
    {
        Exception? failure = null;
        Size shownSize = Size.Empty;
        var maximizeAvailable = true;
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new TestWindow
                {
                    ShowInTaskbar = false,
                    Opacity = 0,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-32000, -32000),
                    Size = new Size(820, 860)
                };
                window.MinimumSize = new Size(820, 560);
                window.MaximumSize = new Size(820, short.MaxValue);
                window.Size = new Size(940, 700);
                window.MaximizeBox = false;
                window.ExposedTitleBar.MaximizeAvailable = false;
                window.Show();
                Application.DoEvents();
                shownSize = window.Size;
                maximizeAvailable = window.ExposedTitleBar.MaximizeAvailable;
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.Equal(820, shownSize.Width);
        Assert.Equal(700, shownSize.Height);
        Assert.False(maximizeAvailable);
    }

    [Theory]
    [InlineData(0, 0, 13)]
    [InlineData(799, 599, 17)]
    [InlineData(799, 300, 11)]
    [InlineData(400, 300, 1)]
    public void Resize_hit_testing_identifies_edges_corners_and_client_area(int x, int y, int expected)
    {
        var result = ModernWindowForm.HitTestResizeBorder(new Point(x, y), new Size(800, 600), thickness: 8);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 300, 1)]
    [InlineData(0, 0, 12)]
    [InlineData(799, 599, 15)]
    public void Resize_hit_testing_can_disable_horizontal_and_corner_resizing(int x, int y, int expected)
    {
        var result = ModernWindowForm.HitTestResizeBorder(
            new Point(x, y),
            new Size(800, 600),
            thickness: 8,
            allowHorizontal: false,
            allowVertical: true);

        Assert.Equal(expected, result);
    }
}
