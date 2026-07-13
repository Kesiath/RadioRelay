using System.Drawing;

namespace RadioRelay.Client.Radio
{
    public sealed class RadioPreset
    {
        public int Channel { get; init; }
        public float Frequency { get; set; }
        public string Passcode { get; set; } = "";
    }

    /// 
    /// One tunable "radio" (or intercom) in the client UI. Frequency, not
    /// channel name, is what determines who you talk to and hear. Passcode
    /// determines who among those on-frequency can actually decrypt you --
    /// leave it blank for an open/unencrypted radio. Each radio has its own
    /// independently-bindable push-to-talk triggers (two independent slots);
    /// whether you're "listening" on a radio is simply a function of its
    /// Volume.
    /// 
    public class RadioChannel
    {
        public const int PresetCount = 9;
        private readonly RadioPreset[] _presets = new RadioPreset[PresetCount];

        public RadioChannel()
        {
            ConfigurePresets(251.000f);
        }

        public string Name { get; set; } = "RADIO";
        public string LocalName { get; set; } = "";
        public string DisplayName => string.IsNullOrWhiteSpace(LocalName) ? Name : LocalName.Trim();
        public float Frequency { get; set; } = 251.000f;
        public float DefaultFrequency { get; private set; } = 251.000f;
        public int SelectedChannel { get; private set; } = 1;
        public float Volume { get; set; } = 1.0f; // 0..1 -- 0 is effectively "not listening"
        public bool IsIntercom { get; set; } = false;

        /// Blank = unencrypted/open. Non-blank = only other radios
        /// with this exact passcode typed in can decrypt this radio's
        /// traffic. Derived into an actual key via NetOption.FromPasscode.
        public string Passcode { get; set; } = "";

        /// Which ear(s) this radio's received audio plays in.
        public RadioEar Ear { get; set; } = RadioEar.Both;

        /// The accent color shown on this radio's on-screen
        /// transmission HUD chip, so multiple simultaneously-active radios
        /// are easy to tell apart at a glance.
        public Color HudColor { get; set; } = Color.FromArgb(90, 160, 235);

        /// Top-left position of this radio's HUD chip, in screen
        /// coordinates. Null means "not customized yet" -- the HUD falls
        /// back to an automatic cascading default position.
        public Point? HudPosition { get; set; }

        public NetOption SelectedNet => NetOption.FromPasscode(Passcode);

        public void ConfigurePresets(
            float defaultFrequency,
            int selectedChannel = 1,
            IEnumerable<RadioPreset>? presets = null,
            float? legacyFrequency = null,
            string? legacyPasscode = null)
        {
            DefaultFrequency = Math.Clamp(defaultFrequency, 2f, 999f);
            for (var index = 0; index < PresetCount; index++)
            {
                _presets[index] = new RadioPreset
                {
                    Channel = index + 1,
                    Frequency = DefaultFrequency,
                    Passcode = ""
                };
            }

            var loadedPreset = false;
            if (presets != null)
            {
                foreach (var preset in presets)
                {
                    if (preset.Channel < 1 || preset.Channel > PresetCount) continue;
                    _presets[preset.Channel - 1].Frequency = NormalizeFrequency(preset.Frequency);
                    _presets[preset.Channel - 1].Passcode = preset.Passcode ?? "";
                    loadedPreset = true;
                }
            }

            if (!loadedPreset && legacyFrequency.HasValue)
            {
                _presets[0].Frequency = NormalizeFrequency(legacyFrequency.Value);
                _presets[0].Passcode = legacyPasscode ?? "";
            }

            SelectedChannel = Math.Clamp(selectedChannel, 1, PresetCount);
            LoadSelectedPreset();
        }

        public void SelectChannel(int channel)
        {
            SaveSelectedPreset();
            SelectedChannel = Math.Clamp(channel, 1, PresetCount);
            LoadSelectedPreset();
        }

        public void SetActiveFrequency(float frequency)
        {
            Frequency = NormalizeFrequency(frequency);
            _presets[SelectedChannel - 1].Frequency = Frequency;
        }

        public void SetActivePasscode(string? passcode)
        {
            Passcode = passcode ?? "";
            _presets[SelectedChannel - 1].Passcode = Passcode;
        }

        public IReadOnlyList<RadioPreset> GetPresetSnapshot()
        {
            SaveSelectedPreset();
            return _presets
                .Select(preset => new RadioPreset
                {
                    Channel = preset.Channel,
                    Frequency = preset.Frequency,
                    Passcode = preset.Passcode
                })
                .ToArray();
        }

        private float NormalizeFrequency(float frequency) =>
            frequency >= 2f && frequency <= 999f ? frequency : DefaultFrequency;

        private void SaveSelectedPreset()
        {
            var preset = _presets[SelectedChannel - 1];
            preset.Frequency = NormalizeFrequency(Frequency);
            preset.Passcode = Passcode ?? "";
        }

        private void LoadSelectedPreset()
        {
            var preset = _presets[SelectedChannel - 1];
            Frequency = preset.Frequency;
            Passcode = preset.Passcode;
        }
    }
}
