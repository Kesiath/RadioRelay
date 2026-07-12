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
    /// A single PTT binding slot as persisted to disk. Type == null
    /// means "this slot isn't bound to anything".
    public class PttSlotSettings
    {
        public PttBindingType? Type { get; set; }
        public int KeyCode { get; set; }
        public Guid DeviceGuid { get; set; }
        public int ButtonIndex { get; set; }
        public string DisplayName { get; set; } = "";
    }

    /// Persisted per-radio state -- everything a user configures
    /// for one radio row, matched back up by radio Name on load.
    public class RadioSettings
    {
        public string Name { get; set; } = "";
        // Machine-local label; deliberately omitted from operational exports.
        public string LocalName { get; set; } = "";
        public float Frequency { get; set; }
        public float Volume { get; set; } = 1f;
        public RadioEar Ear { get; set; } = RadioEar.Both;
        public string Passcode { get; set; } = "";

        public PttSlotSettings PttPrimary { get; set; } = new();
        public PttSlotSettings PttSecondary { get; set; } = new();

        // Stored as an ARGB int rather than System.Drawing.Color, since
        // Color's public shape doesn't round-trip cleanly through
        // System.Text.Json's default reflection-based (de)serialization.
        public int HudColorArgb { get; set; } = -9868684; // default blue-ish, matches RadioChannel's default

        public int? HudX { get; set; }
        public int? HudY { get; set; }
    }

    /// 
    /// Everything the app remembers between runs: server address, callsign,
    /// PTT release delay, audio devices/gain/click volumes, and every
    /// radio's frequency/volume/ear/passcode/PTT bindings/HUD color+position
    /// -- so reopening the app is "just hit Connect", not "set everything up
    /// again from scratch". Stored as plain JSON under
    /// %AppData%\RadioRelay\settings.json. Passcodes are saved in plaintext
    /// here, same as any local app config file (Wi-Fi passwords, browser
    /// saved passwords, etc.) -- this is a local convenience file, not a
    /// secrets vault, and is only ever read by this app on this machine.
    /// 
    public class AppSettings
    {
        public const string ExportFileName = "RadioRelay-settings.json";

        public string ServerIp { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 2302;
        public string ServerPassword { get; set; } = "";
        public string Callsign { get; set; } = "";
        public int PttReleaseDelayMs { get; set; } = 200;
        public bool ControlLockEnabled { get; set; }

        public int InputDeviceIndex { get; set; } = -1;
        public int OutputDeviceIndex { get; set; } = -1;
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
                // Missing/corrupt/unreadable settings file -- fall back to
                // defaults rather than crashing the app on startup.
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
                // Best-effort -- failing to persist settings shouldn't crash
                // the app on close.
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
                        Passcode = imported.Passcode
                    });
                    continue;
                }

                existing.Frequency = imported.Frequency;
                existing.Passcode = imported.Passcode;
            }
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
                Passcode = r.Passcode
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
        }
    }
}
