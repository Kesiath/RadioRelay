using System;

namespace RadioRelay.Client.Input
{
    public enum PttBindingType { Keyboard, JoystickButton, MouseButton }

    public static class MousePttButtons
    {
        public const int XButton1 = 1; // Usually mouse button 4 or Back.
        public const int XButton2 = 2; // Usually mouse button 5 or Forward.

        public static string DisplayName(int button) => button switch
        {
            XButton1 => "Mouse Button 4",
            XButton2 => "Mouse Button 5",
            _ => $"Mouse Button {button}"
        };
    }

    /// <summary>
    /// Stores one keyboard, mouse, or joystick PTT trigger.
    /// </summary>
    public class PttBinding
    {
        public PttBindingType Type { get; set; } = PttBindingType.Keyboard;

        // Keyboard binding.
        public int KeyCode { get; set; } = 0x14; // VK_CAPITAL.

        // Joystick or gamepad binding.
        public Guid DeviceGuid { get; set; } = Guid.Empty;
        public int ButtonIndex { get; set; }

        public string DisplayName { get; set; } = "Keyboard: Caps Lock";

        public override string ToString() => DisplayName;
    }
}
