using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using RadioRelay.Client.Radio;

namespace RadioRelay.Client.Input
{
    /// Which of a radio's two independent PTT slots a binding
    /// occupies. Either slot alone triggers that radio -- e.g. bind Primary
    /// to a HOTAS button and Secondary to a keyboard key, and pressing
    /// either one transmits.
    public enum PttSlot { Primary = 0, Secondary = 1 }

    /// 
    /// Fires PttDown/PttUp -- tagged with which RadioChannel it's for -- for
    /// whichever input (keyboard key or joystick/gamepad button) is bound to
    /// each radio. Every radio has two independent binding slots (Primary +
    /// Secondary); pressing either one transmits on that radio, and holding
    /// both at once still only counts as "down" once (releasing one while
    /// the other is still held does not end the transmission). Every radio's
    /// bindings are independent of every other radio's, so two radios can be
    /// keyed simultaneously if their bindings are physically different
    /// buttons. Also supports a "capture next input" mode, scoped to a
    /// single (channel, slot) pair at a time, for the "Set PTT" flow.
    /// 
    public class PttInputManager : IDisposable
    {
        private const int SlotCount = 2;

        private readonly PttHook _keyboard = new();
        private readonly PttMouseHook _mouse = new();
        private readonly JoystickPoller _joystick = new();

        private readonly Dictionary<RadioChannel, PttBinding?[]> _bindings = new();
        private readonly Dictionary<RadioChannel, bool[]> _slotDown = new();

        private RadioChannel? _capturingChannel;
        private PttSlot _capturingSlot;
        private Action<PttBinding?>? _captureCallback;

        public event Action<RadioChannel>? PttDown;
        public event Action<RadioChannel>? PttUp;

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

        public PttBinding? GetBinding(RadioChannel channel, PttSlot slot) =>
            _bindings.TryGetValue(channel, out var slots) ? slots[(int)slot] : null;

        public void SetBinding(RadioChannel channel, PttSlot slot, PttBinding? binding)
        {
            if (!_bindings.TryGetValue(channel, out var slots))
            {
                slots = new PttBinding?[SlotCount];
                _bindings[channel] = slots;
                _slotDown[channel] = new bool[SlotCount];
            }
            slots[(int)slot] = binding;
            _slotDown[channel][(int)slot] = false;
        }

        /// Listens for the next keyboard key or joystick button
        /// press from any device and reports it back via callback, binding
        /// it to the given (channel, slot). Does not affect any other
        /// binding. Only one capture can be active at a time.
        public void StartCapture(RadioChannel channel, PttSlot slot, Action<PttBinding?> onCaptured)
        {
            _capturingChannel = channel;
            _capturingSlot = slot;
            _captureCallback = onCaptured;
        }

        public void CancelCapture()
        {
            _capturingChannel = null;
            _captureCallback = null;
        }

        internal void HandleRawInputForTest(PttBindingType type, Guid deviceGuid, int code, bool pressed) =>
            OnRawInput(type, deviceGuid, code, pressed);

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
            if (_capturingChannel != null)
            {
                if (!pressed)
                    return; // bind on press, not release

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

            // A single physical input can legitimately be bound to more than
            // one radio (or more than one slot on the same radio, though
            // that's redundant) -- fire for every channel with a matching slot.
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

                // The channel counts as "down" if EITHER slot is currently
                // held -- releasing one bound key while the other is still
                // held must not end the transmission.
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
                catch { /* Input hook callbacks must not be killed by UI shutdown/subscriber failures. */ }
            }
        }

        public void Dispose()
        {
            _keyboard.Dispose();
            _mouse.Dispose();
            _joystick.Dispose();
        }
    }
}
