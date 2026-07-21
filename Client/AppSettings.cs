using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RadioRelay.Client.Diagnostics;
using RadioRelay.Client.Input;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Diagnostics;

namespace RadioRelay.Client
{
    /// <summary>
    /// Stores one PTT binding; a null type means unbound.
    /// </summary>
    public class PttSlotSettings
    {
        public PttBindingType? Type { get; set; }
        public int KeyCode { get; set; }
        public Guid DeviceGuid { get; set; }
        public int ButtonIndex { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public class RadioPresetSettings
    {
        public int Channel { get; set; }
        public string Name { get; set; } = "";
        public float Frequency { get; set; }
        public string Passcode { get; set; } = "";
    }

    /// <summary>
    /// Stores user configuration for one named radio.
    /// </summary>
    public class RadioSettings
    {
        public string Name { get; set; } = "";
        // Machine-local label; deliberately omitted from operational exports.
        public string LocalName { get; set; } = "";
        public float Frequency { get; set; }
        public float Volume { get; set; } = 1f;
        public RadioEar Ear { get; set; } = RadioEar.Both;
        public string Passcode { get; set; } = "";
        public int SelectedChannel { get; set; } = 1;
        public List<RadioPresetSettings> Channels { get; set; } = new();

        public PttSlotSettings PttPrimary { get; set; } = new();
        public PttSlotSettings PttSecondary { get; set; } = new();

        // Store ARGB because Color does not round-trip cleanly through System.Text.Json.
        public int HudColorArgb { get; set; } = -9868684; // Default radio blue.

        public int? HudX { get; set; }
        public int? HudY { get; set; }
    }

    /// <summary>
    /// Stores local application and radio configuration as JSON under
    /// <c>%AppData%\RadioRelay\settings.json</c>. Passcodes are stored in plaintext.
    /// </summary>
    public class AppSettings
    {
        public const string ExportFileName = "RadioRelay-settings.json";

        public string ServerIp { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 2302;
        public string ServerPassword { get; set; } = "";
        public string Callsign { get; set; } = "";
        public int PttReleaseDelayMs { get; set; } = 200;
        public bool ControlLockEnabled { get; set; }
        // ICP controls are machine-local and omitted from profile exports.
        public PttSlotSettings IcpToggle { get; set; } = new();
        public int? IcpX { get; set; }
        public int? IcpY { get; set; }

        public int InputDeviceIndex { get; set; } = -1;
        public int OutputDeviceIndex { get; set; } = -1;
        // Null disables the machine-local virtual-cable output.
        public string? PassthroughDeviceId { get; set; }
        // Retained to migrate index-based passthrough settings to endpoint IDs.
        public int? PassthroughDeviceIndex { get; set; }
        public float PassthroughVolume { get; set; } = 1.0f;
        // Process identity is machine-local; null/empty disables transmitted ambience.
        public string? ApplicationAmbienceExecutablePath { get; set; }
        public string ApplicationAmbienceProcessName { get; set; } = "";
        public float ApplicationAmbienceGain { get; set; } = 0.38f;
        public float InputGain { get; set; } = 1.0f;
        public float InputClickVolume { get; set; } = 1.0f;
        public float TalkOverWarningVolume { get; set; } = 1.0f;
        public float OutputClickVolume { get; set; } = 1.0f;

        public List<RadioSettings> Radios { get; set; } = new();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RadioRelay", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception ex)
            {
                ClientDiagnostics.Current?.LogException(ErrorCodes.ClientSettingsLoadSaveFailure, "settings load failed", ex);
                // Use defaults when settings cannot be loaded.
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                var json = ToJson();
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                ClientDiagnostics.Current?.LogException(ErrorCodes.ClientSettingsLoadSaveFailure, "settings save failed", ex);
                // Settings persistence must not prevent shutdown.
            }
        }

        public string ExportToDirectory(string directoryPath)
        {
            return ExportToFile(Path.Combine(directoryPath, ExportFileName));
        }

        public string ExportToFile(string filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(fullPath, ToExportJson());
            return fullPath;
        }

        public static AppSettings ImportFromFile(string filePath)
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }

        public void CopyFrom(AppSettings other)
        {
            ServerIp = other.ServerIp;
            Port = other.Port;
            ServerPassword = other.ServerPassword;

            MergeOperationalRadioSettings(other.Radios ?? new List<RadioSettings>());
        }

        private void MergeOperationalRadioSettings(List<RadioSettings> importedRadios)
        {
            foreach (var imported in importedRadios)
            {
                var existing = Radios.Find(r => r.Name == imported.Name)
                    ?? (imported.Name == "INTERCOM" ? Radios.Find(r => r.Name == "RADIO 3") : null)
                    ?? (imported.Name == "RADIO 3" ? Radios.Find(r => r.Name == "INTERCOM") : null);

                if (existing == null)
                {
                    Radios.Add(new RadioSettings
                    {
                        Name = imported.Name,
                        Frequency = imported.Frequency,
                        Passcode = imported.Passcode,
                        SelectedChannel = Math.Clamp(imported.SelectedChannel, 1, RadioChannel.PresetCount),
                        Channels = OperationalChannelsFrom(imported)
                    });
                    continue;
                }

                existing.Frequency = imported.Frequency;
                existing.Passcode = imported.Passcode;
                existing.SelectedChannel = Math.Clamp(imported.SelectedChannel, 1, RadioChannel.PresetCount);
                existing.Channels = OperationalChannelsFrom(imported);
            }
        }

        private static List<RadioPresetSettings> OperationalChannelsFrom(RadioSettings imported)
        {
            if (imported.Channels != null && imported.Channels.Count > 0)
            {
                return imported.Channels
                    .Where(channel => channel.Channel >= 1 && channel.Channel <= RadioChannel.PresetCount)
                    .Select(channel => new RadioPresetSettings
                    {
                        Channel = channel.Channel,
                        Name = channel.Name ?? "",
                        Frequency = channel.Frequency,
                        Passcode = channel.Passcode ?? ""
                    })
                    .ToList();
            }

            // Migrate single-channel profiles into preset one.
            return new List<RadioPresetSettings>
            {
                new()
                {
                    Channel = 1,
                    Name = "",
                    Frequency = imported.Frequency,
                    Passcode = imported.Passcode ?? ""
                }
            };
        }

        private string ToJson() =>
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        private string ToExportJson() =>
            JsonSerializer.Serialize(ToExportSettings(), new JsonSerializerOptions { WriteIndented = true });

        private OperationalExportSettings ToExportSettings() => new()
        {
            ServerIp = ServerIp,
            Port = Port,
            ServerPassword = ServerPassword,
            Radios = Radios.ConvertAll(r => new OperationalExportRadioSettings
            {
                Name = r.Name,
                Frequency = r.Frequency,
                Passcode = r.Passcode,
                SelectedChannel = r.SelectedChannel,
                Channels = OperationalChannelsFrom(r).ConvertAll(channel => new OperationalExportPresetSettings
                {
                    Channel = channel.Channel,
                    Name = channel.Name,
                    Frequency = channel.Frequency,
                    Passcode = channel.Passcode
                })
            })
        };

        private sealed class OperationalExportSettings
        {
            public string ServerIp { get; set; } = "127.0.0.1";
            public int Port { get; set; } = 2302;
            public string ServerPassword { get; set; } = "";
            public List<OperationalExportRadioSettings> Radios { get; set; } = new();
        }

        private sealed class OperationalExportRadioSettings
        {
            public string Name { get; set; } = "";
            public float Frequency { get; set; }
            public string Passcode { get; set; } = "";
            public int SelectedChannel { get; set; } = 1;
            public List<OperationalExportPresetSettings> Channels { get; set; } = new();
        }

        private sealed class OperationalExportPresetSettings
        {
            public int Channel { get; set; }
            public string Name { get; set; } = "";
            public float Frequency { get; set; }
            public string Passcode { get; set; } = "";
        }
    }
}
