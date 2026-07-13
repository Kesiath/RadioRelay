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

    [Fact]
    public void Icp_toggle_fires_once_per_press_instead_of_repeating_while_held()
    {
        using var manager = new PttInputManager();
        int toggles = 0;
        manager.IcpTogglePressed += () => toggles++;
        manager.SetIcpToggleBinding(new PttBinding
        {
            Type = PttBindingType.Keyboard,
            KeyCode = (int)System.Windows.Forms.Keys.F10,
            DisplayName = "Keyboard: F10"
        });

        manager.HandleRawInputForTest(PttBindingType.Keyboard, Guid.Empty, (int)System.Windows.Forms.Keys.F10, pressed: true);
        manager.HandleRawInputForTest(PttBindingType.Keyboard, Guid.Empty, (int)System.Windows.Forms.Keys.F10, pressed: true);
        manager.HandleRawInputForTest(PttBindingType.Keyboard, Guid.Empty, (int)System.Windows.Forms.Keys.F10, pressed: false);
        manager.HandleRawInputForTest(PttBindingType.Keyboard, Guid.Empty, (int)System.Windows.Forms.Keys.F10, pressed: true);

        Assert.Equal(2, toggles);
    }

    [Fact]
    public void Escape_during_icp_capture_clears_the_binding()
    {
        using var manager = new PttInputManager();
        manager.SetIcpToggleBinding(new PttBinding { KeyCode = (int)System.Windows.Forms.Keys.F10 });
        PttBinding? captured = new();

        manager.StartIcpToggleCapture(binding => captured = binding);
        manager.HandleRawInputForTest(PttBindingType.Keyboard, Guid.Empty, (int)System.Windows.Forms.Keys.Escape, pressed: true);

        Assert.Null(captured);
        Assert.Null(manager.GetIcpToggleBinding());
    }

    [Fact]
    public void Escape_fires_once_per_press_for_global_overlay_dismissal()
    {
        using var manager = new PttInputManager();
        int presses = 0;
        manager.EscapePressed += () => presses++;

        manager.HandleRawInputForTest(PttBindingType.Keyboard, Guid.Empty, (int)System.Windows.Forms.Keys.Escape, pressed: true);
        manager.HandleRawInputForTest(PttBindingType.Keyboard, Guid.Empty, (int)System.Windows.Forms.Keys.Escape, pressed: true);
        manager.HandleRawInputForTest(PttBindingType.Keyboard, Guid.Empty, (int)System.Windows.Forms.Keys.Escape, pressed: false);
        manager.HandleRawInputForTest(PttBindingType.Keyboard, Guid.Empty, (int)System.Windows.Forms.Keys.Escape, pressed: true);

        Assert.Equal(2, presses);
    }
}
