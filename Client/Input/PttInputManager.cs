using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using RadioRelay.Client.Radio;

namespace RadioRelay.Client.Input
{
    /// <summary>
    /// Identifies one of a radio's two independent PTT bindings.
    /// </summary>
    public enum PttSlot { Primary = 0, Secondary = 1 }

    /// <summary>
    /// Maps keyboard, mouse, and joystick input to per-radio PTT state and
    /// supports capture of new bindings.
    /// </summary>
    public class PttInputManager : IDisposable
    {
        private const int SlotCount = 2;

        private readonly PttHook _keyboard = new();
        private readonly PttMouseHook _mouse = new();
        private readonly JoystickPoller _joystick = new();

        private readonly Dictionary<RadioChannel, PttBinding?[]> _bindings = new();
        private readonly Dictionary<RadioChannel, bool[]> _slotDown = new();
        private readonly object _stateLock = new();

        private RadioChannel? _capturingChannel;
        private PttSlot _capturingSlot;
        private Action<PttBinding?>? _captureCallback;
        private bool _capturingIcpToggle;
        private PttBinding? _icpToggleBinding;
        private bool _icpToggleDown;
        private bool _escapeDown;

        public event Action<RadioChannel>? PttDown;
        public event Action<RadioChannel>? PttUp;
        public event Action? IcpTogglePressed;
        public event Action? EscapePressed;

        public void Start()
        {
            _keyboard.Start();
            _mouse.Start();
            _joystick.Start();

            _keyboard.KeyDown += vk => OnRawInput(PttBindingType.Keyboard, Guid.Empty, vk, true);
            _keyboard.KeyUp += vk => OnRawInput(PttBindingType.Keyboard, Guid.Empty, vk, false);
            _mouse.ButtonDown += button => OnRawInput(PttBindingType.MouseButton, Guid.Empty, button, true);
            _mouse.ButtonUp += button => OnRawInput(PttBindingType.MouseButton, Guid.Empty, button, false);
            _joystick.ButtonChanged += (_, e) =>
                OnRawInput(PttBindingType.JoystickButton, e.DeviceGuid, e.ButtonIndex, e.Pressed);
        }

        public IEnumerable<(Guid Guid, string Name)> GetJoystickDevices() => _joystick.GetDeviceNames();

        public PttBinding? GetBinding(RadioChannel channel, PttSlot slot)
        {
            lock (_stateLock)
                return _bindings.TryGetValue(channel, out var slots) ? slots[(int)slot] : null;
        }

        public void SetBinding(RadioChannel channel, PttSlot slot, PttBinding? binding)
        {
            lock (_stateLock)
                SetBindingLocked(channel, slot, binding);
        }

        private void SetBindingLocked(RadioChannel channel, PttSlot slot, PttBinding? binding)
        {
            if (!_bindings.TryGetValue(channel, out var slots))
            {
                slots = new PttBinding?[SlotCount];
                _bindings[channel] = slots;
                _slotDown[channel] = new bool[SlotCount];
            }

            bool wasDownOverall = _channelWasDown.TryGetValue(channel, out var previous) && previous;
            slots[(int)slot] = binding;
            _slotDown[channel][(int)slot] = false;
            bool isDownOverall = _slotDown[channel][0] || _slotDown[channel][1];
            _channelWasDown[channel] = isDownOverall;

            // Release a keyed radio when rebinding removes its last held slot.
            if (wasDownOverall && !isDownOverall)
                SafeInvoke(PttUp, channel);
        }

        /// <summary>
        /// Captures the next input for one channel and slot.
        /// </summary>
        public void StartCapture(RadioChannel channel, PttSlot slot, Action<PttBinding?> onCaptured)
        {
            lock (_stateLock)
            {
                ReleaseAllPttStatesLocked();
                _capturingIcpToggle = false;
                _capturingChannel = channel;
                _capturingSlot = slot;
                _captureCallback = onCaptured;
            }
        }

        public PttBinding? GetIcpToggleBinding()
        {
            lock (_stateLock) return _icpToggleBinding;
        }

        public void SetIcpToggleBinding(PttBinding? binding)
        {
            lock (_stateLock)
            {
                _icpToggleBinding = binding;
                _icpToggleDown = false;
            }
        }

        public void StartIcpToggleCapture(Action<PttBinding?> onCaptured)
        {
            lock (_stateLock)
            {
                ReleaseAllPttStatesLocked();
                _capturingChannel = null;
                _capturingIcpToggle = true;
                _captureCallback = onCaptured;
            }
        }

        public void CancelCapture()
        {
            lock (_stateLock)
            {
                _capturingChannel = null;
                _capturingIcpToggle = false;
                _captureCallback = null;
            }
        }

        internal void HandleRawInputForTest(PttBindingType type, Guid deviceGuid, int code, bool pressed) =>
            OnRawInput(type, deviceGuid, code, pressed);

        internal void ReleaseAllPttStates()
        {
            lock (_stateLock)
                ReleaseAllPttStatesLocked();
        }

        private void ReleaseAllPttStatesLocked()
        {
            foreach (var channel in _bindings.Keys.ToList())
            {
                bool wasDown = _channelWasDown.TryGetValue(channel, out var previous) && previous;
                if (_slotDown.TryGetValue(channel, out var slots))
                    Array.Clear(slots, 0, slots.Length);
                _channelWasDown[channel] = false;
                if (wasDown) SafeInvoke(PttUp, channel);
            }
        }

        private static PttBinding CreateBinding(PttBindingType type, Guid deviceGuid, int code) => type switch
        {
            PttBindingType.Keyboard => new PttBinding
            {
                Type = PttBindingType.Keyboard,
                KeyCode = code,
                DisplayName = $"Keyboard: {(Keys)code}"
            },
            PttBindingType.MouseButton => new PttBinding
            {
                Type = PttBindingType.MouseButton,
                ButtonIndex = code,
                DisplayName = MousePttButtons.DisplayName(code)
            },
            _ => new PttBinding
            {
                Type = PttBindingType.JoystickButton,
                DeviceGuid = deviceGuid,
                ButtonIndex = code,
                DisplayName = $"Joystick button {code + 1}"
            }
        };

        private void OnRawInput(PttBindingType type, Guid deviceGuid, int code, bool pressed)
        {
            lock (_stateLock)
                OnRawInputLocked(type, deviceGuid, code, pressed);
        }

        private void OnRawInputLocked(PttBindingType type, Guid deviceGuid, int code, bool pressed)
        {
            if (_capturingIcpToggle)
            {
                if (!pressed) return;

                var callback = _captureCallback;
                _capturingIcpToggle = false;
                _captureCallback = null;

                if (type == PttBindingType.Keyboard && code == (int)Keys.Escape)
                {
                    SetIcpToggleBinding(null);
                    SafeInvoke(callback, null);
                    return;
                }

                var binding = CreateBinding(type, deviceGuid, code);
                SetIcpToggleBinding(binding);
                SafeInvoke(callback, binding);
                return;
            }

            if (_capturingChannel != null)
            {
                if (!pressed)
                    return; // Bind on press, not release.

                var callback = _captureCallback;
                var channel = _capturingChannel;
                var slot = _capturingSlot;

                _capturingChannel = null;
                _captureCallback = null;

                // Escape clears the binding instead of assigning it.
                if (type == PttBindingType.Keyboard && code == (int)Keys.Escape)
                {
                    SetBinding(channel, slot, null);
                    SafeInvoke(callback, null);
                    return;
                }

                var binding = CreateBinding(type, deviceGuid, code);

                SetBinding(channel, slot, binding);
                SafeInvoke(callback, binding);
                return;
            }

            if (type == PttBindingType.Keyboard && code == (int)Keys.Escape)
            {
                bool wasDown = _escapeDown;
                _escapeDown = pressed;
                if (pressed && !wasDown) SafeInvoke(EscapePressed);
            }

            if (_icpToggleBinding != null &&
                type == _icpToggleBinding.Type &&
                BindingMatches(type, deviceGuid, code, _icpToggleBinding))
            {
                bool wasDown = _icpToggleDown;
                _icpToggleDown = pressed;
                if (pressed && !wasDown) SafeInvoke(IcpTogglePressed);
            }

            // Apply one physical input to every matching radio binding.
            foreach (var kvp in _bindings.ToList())
            {
                var channel = kvp.Key;
                var slots = kvp.Value;
                var downFlags = _slotDown[channel];

                bool anyMatchedThisChannel = false;
                for (int i = 0; i < SlotCount; i++)
                {
                    var binding = slots[i];
                    if (binding == null) continue;

                    bool matches = type == binding.Type && BindingMatches(type, deviceGuid, code, binding);
                    if (!matches) continue;

                    anyMatchedThisChannel = true;
                    downFlags[i] = pressed;
                }

                if (!anyMatchedThisChannel) continue;

                // Keep the channel keyed while either slot remains held.
                bool wasDownOverall = _channelWasDown.TryGetValue(channel, out var prev) && prev;
                bool isDownOverall = downFlags[0] || downFlags[1];

                if (isDownOverall && !wasDownOverall) SafeInvoke(PttDown, channel);
                else if (!isDownOverall && wasDownOverall) SafeInvoke(PttUp, channel);

                _channelWasDown[channel] = isDownOverall;
            }
        }

        private readonly Dictionary<RadioChannel, bool> _channelWasDown = new();

        private static bool BindingMatches(PttBindingType type, Guid deviceGuid, int code, PttBinding binding) => type switch
        {
            PttBindingType.Keyboard => code == binding.KeyCode,
            PttBindingType.MouseButton => code == binding.ButtonIndex,
            _ => deviceGuid == binding.DeviceGuid && code == binding.ButtonIndex
        };

        private static void SafeInvoke<T>(Action<T>? handler, T arg)
        {
            if (handler == null) return;

            foreach (Action<T> subscriber in handler.GetInvocationList().Cast<Action<T>>())
            {
                try { subscriber(arg); }
                catch
                {
                    // Isolate input hooks from subscriber and shutdown failures.
                }
            }
        }

        private static void SafeInvoke(Action? handler)
        {
            if (handler == null) return;
            foreach (Action subscriber in handler.GetInvocationList().Cast<Action>())
            {
                try { subscriber(); }
                catch { }
            }
        }

        public void Dispose()
        {
            ReleaseAllPttStates();
            _keyboard.Dispose();
            _mouse.Dispose();
            _joystick.Dispose();
        }
    }
}
