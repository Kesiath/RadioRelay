using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SharpDX.DirectInput;

namespace RadioRelay.Client.Input
{
    public class JoystickButtonEventArgs : EventArgs
    {
        public Guid DeviceGuid = Guid.Empty;
        public string DeviceName = "";
        public int ButtonIndex;
        public bool Pressed;
    }

    /// 
    /// Polls every connected DirectInput joystick/gamepad/HOTAS device for
    /// button state changes. DirectInput (rather than XInput) is used
    /// deliberately -- XInput only covers Xbox-style controllers, while
    /// DirectInput covers arbitrary flight sticks, throttles and pedals,
    /// which is what most DCS/TFAR-style users actually bind PTT to. Note:
    /// some Xbox/XInput-only pads expose buttons inconsistently through
    /// DirectInput -- see README.md if yours doesn't show up.
    /// 
    public class JoystickPoller : IDisposable
    {
        private readonly DirectInput _directInput = new();
        private readonly Dictionary<Guid, Joystick> _devices = new();
        private readonly Dictionary<Guid, bool[]> _lastButtonStates = new();
        private System.Threading.Timer? _pollTimer;

        public event EventHandler<JoystickButtonEventArgs>? ButtonChanged;

        public void Start()
        {
            RefreshDevices();
            _pollTimer = new System.Threading.Timer(_ => Poll(), null, 0, 15);
        }

        /// Call periodically (or on demand from the UI) to pick up
        /// devices plugged in after startup.
        public void RefreshDevices()
        {
            var found = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
            foreach (var deviceInfo in found)
            {
                if (_devices.ContainsKey(deviceInfo.InstanceGuid)) continue;
                try
                {
                    var joystick = new Joystick(_directInput, deviceInfo.InstanceGuid);
                    joystick.Properties.BufferSize = 128;
                    joystick.Acquire();
                    _devices[deviceInfo.InstanceGuid] = joystick;
                    _lastButtonStates[deviceInfo.InstanceGuid] = Array.Empty<bool>();
                }
                catch
                {
                    // Device busy or inaccessible right now -- next refresh can retry.
                }
            }
        }

        public IEnumerable<(Guid Guid, string Name)> GetDeviceNames()
        {
            RefreshDevices();
            return _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
                .Select(d => (d.InstanceGuid, d.InstanceName));
        }

        private void Poll()
        {
            foreach (var kvp in _devices.ToList())
            {
                var guid = kvp.Key;
                var joystick = kvp.Value;
                JoystickState state;
                try
                {
                    joystick.Poll();
                    state = joystick.GetCurrentState();
                }
                catch
                {
                    continue; // device unplugged etc -- leave it, don't crash the poll loop
                }

                var buttons = state.Buttons;
                if (!_lastButtonStates.TryGetValue(guid, out var last) || last.Length != buttons.Length)
                    last = new bool[buttons.Length];

                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i] != last[i])
                    {
                        ButtonChanged?.Invoke(this, new JoystickButtonEventArgs
                        {
                            DeviceGuid = guid,
                            DeviceName = joystick.Information.InstanceName,
                            ButtonIndex = i,
                            Pressed = buttons[i]
                        });
                    }
                }

                _lastButtonStates[guid] = buttons;
            }
        }

        public void Dispose()
        {
            _pollTimer?.Dispose();
            foreach (var d in _devices.Values) d.Dispose();
            _directInput.Dispose();
        }
    }
}
