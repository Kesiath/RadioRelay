using System.Reflection;
using System.Windows.Forms;
using RadioRelay.Client.Radio;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class IcpOverlayWindowTests
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [Fact]
    public void Icp_overlay_is_a_nonactivating_tool_window()
    {
        using var overlay = new IcpOverlayForm([]);
        var showWithoutActivation = typeof(IcpOverlayForm).GetProperty(
            "ShowWithoutActivation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var createParams = typeof(IcpOverlayForm).GetProperty(
            "CreateParams",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(showWithoutActivation);
        Assert.NotNull(createParams);
        Assert.True((bool)showWithoutActivation!.GetValue(overlay)!);

        var parameters = Assert.IsType<CreateParams>(createParams!.GetValue(overlay));
        Assert.NotEqual(0, parameters.ExStyle & WS_EX_TOOLWINDOW);
        Assert.NotEqual(0, parameters.ExStyle & WS_EX_NOACTIVATE);
    }

    [Fact]
    public void Icp_overlay_rejects_mouse_activation()
    {
        using var overlay = new IcpOverlayForm([]);
        var wndProc = typeof(IcpOverlayForm).GetMethod(
            "WndProc",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var arguments = new object[]
        {
            Message.Create(IntPtr.Zero, 0x0021, IntPtr.Zero, IntPtr.Zero)
        };

        Assert.NotNull(wndProc);
        wndProc!.Invoke(overlay, arguments);

        var message = Assert.IsType<Message>(arguments[0]);
        Assert.Equal((IntPtr)3, message.Result);
    }

    [Fact]
    public void Icp_volume_slider_does_not_request_focus_on_click()
    {
        using var overlay = new IcpOverlayForm([]);
        var sliderField = typeof(IcpOverlayForm).GetField(
            "_volumeSlider",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var slider = Assert.IsType<ModernSlider>(sliderField!.GetValue(overlay));
        Assert.False(slider.FocusOnPointerInteraction);
        Assert.False(slider.TabStop);
    }

    [Fact]
    public void Icp_channel_buttons_show_the_selected_radios_channel_names()
    {
        var radio = new RadioChannel();
        radio.SetActiveChannelName("Guard");
        using var overlay = new IcpOverlayForm([radio]);
        var selectRadio = typeof(IcpOverlayForm).GetMethod("SelectRadio", BindingFlags.Instance | BindingFlags.NonPublic);
        var selectChannel = typeof(IcpOverlayForm).GetMethod("SelectChannel", BindingFlags.Instance | BindingFlags.NonPublic);
        var confirm = typeof(IcpOverlayForm).GetMethod("Confirm", BindingFlags.Instance | BindingFlags.NonPublic);
        var channelButtonsField = typeof(IcpOverlayForm).GetField("_channelButtons", BindingFlags.Instance | BindingFlags.NonPublic);

        selectRadio!.Invoke(overlay, [radio]);

        var buttons = Assert.IsType<List<ModernButton>>(channelButtonsField!.GetValue(overlay));
        Assert.Equal("1 — Guard", buttons[0].Text);
        Assert.Equal("2", buttons[1].Text);

        selectChannel!.Invoke(overlay, [1]);
        confirm!.Invoke(overlay, null);

        Assert.Equal("1", buttons[0].Text);
        Assert.Equal("2", buttons[1].Text);
    }
}
