using System;

namespace RadioRelay.Client.Input
{
    public enum PttBindingType { Keyboard, JoystickButton, MouseButton }

    public static class MousePttButtons
    {
        public const int XButton1 = 1; // Windows XBUTTON1, typically physical mouse button 4 / Back
        public const int XButton2 = 2; // Windows XBUTTON2, typically physical mouse button 5 / Forward

        public static string DisplayName(int button) => button switch
        {
            XButton1 => "Mouse Button 4",
            XButton2 => "Mouse Button 5",
            _ => $"Mouse Button {button}"
        };
    }

    /// A single configured PTT trigger -- one keyboard key, one
    /// mouse side button, or one button on one specific joystick/gamepad device.
    public class PttBinding
    {
        public PttBindingType Type { get; set; } = PttBindingType.Keyboard;

        // Keyboard
        public int KeyCode { get; set; } = 0x14; // VK_CAPITAL

        // Joystick/gamepad
        public Guid DeviceGuid { get; set; } = Guid.Empty;
        public int ButtonIndex { get; set; }

        public string DisplayName { get; set; } = "Keyboard: Caps Lock";

        public override string ToString() => DisplayName;
    }
}
