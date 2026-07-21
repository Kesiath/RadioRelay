using System.Drawing;

namespace RadioRelay.Client.Radio
{
    public sealed class RadioPreset
    {
        public int Channel { get; init; }
        public string Name { get; set; } = "";
        public float Frequency { get; set; }
        public string Passcode { get; set; } = "";
    }

    /// <summary>
    /// Models one tunable radio or intercom with presets, routing, security,
    /// volume, and HUD configuration.
    /// </summary>
    public class RadioChannel
    {
        public const int PresetCount = 9;
        public const float MaxReceiveVolume = 3.0f;
        private readonly RadioPreset[] _presets = new RadioPreset[PresetCount];
        private float _volume = 1.0f;

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
        public string SelectedChannelName => _presets[SelectedChannel - 1].Name;
        public float Volume
        {
            get => _volume;
            set => _volume = Math.Clamp(value, 0f, MaxReceiveVolume);
        }
        public bool IsIntercom { get; set; } = false;

        /// <summary>
        /// Passcode used to derive the selected net; blank means unencrypted.
        /// </summary>
        public string Passcode { get; set; } = "";

        /// <summary>
        /// Output channel for received audio.
        /// </summary>
        public RadioEar Ear { get; set; } = RadioEar.Both;

        /// <summary>
        /// Accent color for this radio's HUD activity chip.
        /// </summary>
        public Color HudColor { get; set; } = Color.FromArgb(90, 160, 235);

        /// <summary>
        /// Custom HUD chip position; null uses automatic placement.
        /// </summary>
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
                    Name = "",
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
                    _presets[preset.Channel - 1].Name = preset.Name ?? "";
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

        public void SetActiveChannelName(string? name)
        {
            _presets[SelectedChannel - 1].Name = name ?? "";
        }

        public string GetChannelDisplayName(int channel)
        {
            var channelNumber = Math.Clamp(channel, 1, PresetCount);
            var name = _presets[channelNumber - 1].Name.Trim();
            return string.IsNullOrEmpty(name) ? channelNumber.ToString() : $"{channelNumber} — {name}";
        }

        public IReadOnlyList<RadioPreset> GetPresetSnapshot()
        {
            SaveSelectedPreset();
            return _presets
                .Select(preset => new RadioPreset
                {
                    Channel = preset.Channel,
                    Name = preset.Name,
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
