using RadioRelay.Client.Input;
using RadioRelay.Client.Radio;

namespace RadioRelay.Tests;

public class PttMouseButtonTests
{
    [Fact]
    public void Capture_mouse_button_4_creates_mouse_ptt_binding_with_human_display_name()
    {
        using var manager = new PttInputManager();
        var channel = new RadioChannel { Name = "RADIO 1" };
        PttBinding? captured = null;

        manager.StartCapture(channel, PttSlot.Primary, binding => captured = binding);
        manager.HandleRawInputForTest(PttBindingType.MouseButton, Guid.Empty, MousePttButtons.XButton1, pressed: true);

        Assert.NotNull(captured);
        Assert.Equal(PttBindingType.MouseButton, captured.Type);
        Assert.Equal(MousePttButtons.XButton1, captured.ButtonIndex);
        Assert.Equal("Mouse Button 4", captured.DisplayName);
        Assert.Same(captured, manager.GetBinding(channel, PttSlot.Primary));
    }

    [Fact]
    public void Bound_mouse_button_5_triggers_ptt_down_and_up()
    {
        using var manager = new PttInputManager();
        var channel = new RadioChannel { Name = "RADIO 1" };
        var events = new List<string>();
        manager.PttDown += ch => events.Add($"down:{ch.Name}");
        manager.PttUp += ch => events.Add($"up:{ch.Name}");
        manager.SetBinding(channel, PttSlot.Primary, new PttBinding
        {
            Type = PttBindingType.MouseButton,
            ButtonIndex = MousePttButtons.XButton2,
            DisplayName = "Mouse Button 5"
        });

        manager.HandleRawInputForTest(PttBindingType.MouseButton, Guid.Empty, MousePttButtons.XButton2, pressed: true);
        manager.HandleRawInputForTest(PttBindingType.MouseButton, Guid.Empty, MousePttButtons.XButton2, pressed: false);

        Assert.Equal(new[] { "down:RADIO 1", "up:RADIO 1" }, events);
    }
}
