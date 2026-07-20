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

    /// <summary>
    /// Polls DirectInput devices for button changes, including HOTAS hardware
    /// not exposed through XInput.
    /// </summary>
    public class JoystickPoller : IDisposable
    {
        private readonly DirectInput _directInput = new();
        private readonly Dictionary<Guid, Joystick> _devices = new();
        private readonly Dictionary<Guid, bool[]> _lastButtonStates = new();
        private readonly object _deviceLock = new();
        private System.Threading.Timer? _pollTimer;
        private int _polling;
        private int _pollCount;
        private volatile bool _disposed;

        public event EventHandler<JoystickButtonEventArgs>? ButtonChanged;

        public void Start()
        {
            RefreshDevices();
            _pollTimer = new System.Threading.Timer(_ => Poll(), null, 0, 15);
        }

        /// <summary>
        /// Refreshes devices connected after startup.
        /// </summary>
        public void RefreshDevices()
        {
            if (_disposed) return;
            lock (_deviceLock)
            {
                if (_disposed) return;
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
                        // Retry unavailable devices on the next refresh.
                    }
                }
            }
        }

        public IEnumerable<(Guid Guid, string Name)> GetDeviceNames()
        {
            RefreshDevices();
            lock (_deviceLock)
            {
                if (_disposed) return Array.Empty<(Guid, string)>();
                return _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
                    .Select(d => (d.InstanceGuid, d.InstanceName))
                    .ToArray();
            }
        }

        private void Poll()
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _polling, 1) != 0) return;
            try
            {
                if (_disposed) return;
                if (++_pollCount % 133 == 0)
                    RefreshDevices();

                KeyValuePair<Guid, Joystick>[] devices;
                lock (_deviceLock)
                    devices = _devices.ToArray();

                foreach (var kvp in devices)
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
                        // Release held buttons before removing an unavailable device.
                        bool[]? heldButtons;
                        lock (_deviceLock)
                            _lastButtonStates.TryGetValue(guid, out heldButtons);
                        if (heldButtons != null)
                        {
                            for (int i = 0; i < heldButtons.Length; i++)
                            {
                                if (!heldButtons[i]) continue;
                                ButtonChanged?.Invoke(this, new JoystickButtonEventArgs
                                {
                                    DeviceGuid = guid,
                                    DeviceName = joystick.Information.InstanceName,
                                    ButtonIndex = i,
                                    Pressed = false
                                });
                            }
                        }

                        lock (_deviceLock)
                        {
                            _lastButtonStates.Remove(guid);
                            _devices.Remove(guid);
                        }
                        try { joystick.Dispose(); } catch { }
                        continue;
                    }

                    var buttons = state.Buttons;
                    bool[] last;
                    lock (_deviceLock)
                    {
                        if (!_lastButtonStates.TryGetValue(guid, out last!) || last.Length != buttons.Length)
                            last = new bool[buttons.Length];
                    }

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

                    lock (_deviceLock)
                    {
                        if (_devices.ContainsKey(guid))
                            _lastButtonStates[guid] = buttons;
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _polling, 0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pollTimer?.Dispose();
            SpinWait.SpinUntil(() => Volatile.Read(ref _polling) == 0, TimeSpan.FromSeconds(1));
            lock (_deviceLock)
            {
                foreach (var d in _devices.Values) d.Dispose();
                _devices.Clear();
                _lastButtonStates.Clear();
                _directInput.Dispose();
            }
        }
    }
}
