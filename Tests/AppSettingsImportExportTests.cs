using System.Text.Json;
using RadioRelay.Client;
using RadioRelay.Client.Input;
using RadioRelay.Client.Radio;

namespace RadioRelay.Tests;

public class AppSettingsImportExportTests
{
    [Fact]
    public void ExportToDirectory_writes_only_operational_drop_in_settings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RadioRelayExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new AppSettings
        {
            ServerIp = "10.0.0.5",
            Port = 5060,
            ServerPassword = "briefing-key",
            Callsign = "Uzi 1",
            PttReleaseDelayMs = 450,
            ControlLockEnabled = true,
            InputDeviceIndex = 2,
            OutputDeviceIndex = 3,
            InputGain = 1.4f,
            InputClickVolume = 0.25f,
            TalkOverWarningVolume = 0.35f,
            OutputClickVolume = 0.45f,
            Radios = new List<RadioSettings>
            {
                new()
                {
                    Name = "RADIO 1",
                    LocalName = "Guard",
                    Frequency = 251.000f,
                    Volume = 0.75f,
                    Ear = RadioEar.Left,
                    Passcode = "red",
                    HudColorArgb = 123,
                    HudX = 11,
                    HudY = 22,
                    PttPrimary = new PttSlotSettings { Type = PttBindingType.Keyboard, KeyCode = 65, DisplayName = "A" }
                }
            }
        };

        var exportedPath = settings.ExportToDirectory(dir);

        Assert.Equal(Path.Combine(dir, AppSettings.ExportFileName), exportedPath);
        Assert.True(File.Exists(exportedPath));
        using var document = JsonDocument.Parse(File.ReadAllText(exportedPath));
        var root = document.RootElement;

        Assert.Equal("10.0.0.5", root.GetProperty("ServerIp").GetString());
        Assert.Equal(5060, root.GetProperty("Port").GetInt32());
        Assert.Equal("briefing-key", root.GetProperty("ServerPassword").GetString());

        var radio = Assert.Single(root.GetProperty("Radios").EnumerateArray());
        Assert.Equal("RADIO 1", radio.GetProperty("Name").GetString());
        Assert.Equal(251.000f, radio.GetProperty("Frequency").GetSingle(), precision: 3);
        Assert.Equal("red", radio.GetProperty("Passcode").GetString());

        Assert.False(root.TryGetProperty("Callsign", out _));
        Assert.False(root.TryGetProperty("PttReleaseDelayMs", out _));
        Assert.False(root.TryGetProperty("ControlLockEnabled", out _));
        Assert.False(root.TryGetProperty("InputDeviceIndex", out _));
        Assert.False(root.TryGetProperty("OutputDeviceIndex", out _));
        Assert.False(root.TryGetProperty("InputGain", out _));
        Assert.False(root.TryGetProperty("InputClickVolume", out _));
        Assert.False(root.TryGetProperty("TalkOverWarningVolume", out _));
        Assert.False(root.TryGetProperty("OutputClickVolume", out _));
        Assert.False(radio.TryGetProperty("Volume", out _));
        Assert.False(radio.TryGetProperty("Ear", out _));
        Assert.False(radio.TryGetProperty("PttPrimary", out _));
        Assert.False(radio.TryGetProperty("PttSecondary", out _));
        Assert.False(radio.TryGetProperty("HudColorArgb", out _));
        Assert.False(radio.TryGetProperty("HudX", out _));
        Assert.False(radio.TryGetProperty("HudY", out _));
        Assert.False(radio.TryGetProperty("LocalName", out _));
    }

    [Fact]
    public void ImportFromFile_reads_exported_operational_settings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RadioRelayImportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var original = new AppSettings
        {
            ServerIp = "192.168.1.20",
            Port = 2302,
            ServerPassword = "operation",
            Callsign = "Overlord",
            InputGain = 1.4f,
            Radios = new List<RadioSettings>
            {
                new() { Name = "RADIO 2", Frequency = 305.500f, Volume = 0.5f, Ear = RadioEar.Right, Passcode = "blue" }
            }
        };
        var exportedPath = original.ExportToDirectory(dir);

        var imported = AppSettings.ImportFromFile(exportedPath);

        Assert.Equal("192.168.1.20", imported.ServerIp);
        Assert.Equal(2302, imported.Port);
        Assert.Equal("operation", imported.ServerPassword);
        Assert.Equal(string.Empty, imported.Callsign);
        Assert.Equal(1.0f, imported.InputGain);
        var radio = Assert.Single(imported.Radios);
        Assert.Equal("RADIO 2", radio.Name);
        Assert.Equal(305.500f, radio.Frequency);
        Assert.Equal("blue", radio.Passcode);
        Assert.Equal(1.0f, radio.Volume);
        Assert.Equal(RadioEar.Both, radio.Ear);
    }

    [Fact]
    public void CopyFrom_imports_operational_settings_without_overwriting_local_controls()
    {
        var local = new AppSettings
        {
            ServerIp = "old.host",
            Port = 1111,
            ServerPassword = "old-password",
            Callsign = "LocalPilot",
            PttReleaseDelayMs = 650,
            ControlLockEnabled = true,
            InputDeviceIndex = 4,
            OutputDeviceIndex = 5,
            InputGain = 1.6f,
            InputClickVolume = 0.2f,
            TalkOverWarningVolume = 0.3f,
            OutputClickVolume = 0.4f,
            Radios = new List<RadioSettings>
            {
                new()
                {
                    Name = "RADIO 1",
                    LocalName = "Local Guard",
                    Frequency = 240.000f,
                    Volume = 0.45f,
                    Ear = RadioEar.Left,
                    Passcode = "old-red",
                    HudColorArgb = 456,
                    HudX = 33,
                    HudY = 44,
                    PttPrimary = new PttSlotSettings { Type = PttBindingType.Keyboard, KeyCode = 66, DisplayName = "B" }
                }
            }
        };
        var imported = new AppSettings
        {
            ServerIp = "new.host",
            Port = 2222,
            ServerPassword = "new-password",
            Callsign = "ImportedPilot",
            InputGain = 0.1f,
            Radios = new List<RadioSettings>
            {
                new() { Name = "RADIO 1", Frequency = 251.000f, Volume = 1.0f, Ear = RadioEar.Both, Passcode = "new-red" }
            }
        };

        local.CopyFrom(imported);

        Assert.Equal("new.host", local.ServerIp);
        Assert.Equal(2222, local.Port);
        Assert.Equal("new-password", local.ServerPassword);
        Assert.Equal("LocalPilot", local.Callsign);
        Assert.Equal(650, local.PttReleaseDelayMs);
        Assert.True(local.ControlLockEnabled);
        Assert.Equal(4, local.InputDeviceIndex);
        Assert.Equal(5, local.OutputDeviceIndex);
        Assert.Equal(1.6f, local.InputGain);
        Assert.Equal(0.2f, local.InputClickVolume);
        Assert.Equal(0.3f, local.TalkOverWarningVolume);
        Assert.Equal(0.4f, local.OutputClickVolume);

        var radio = Assert.Single(local.Radios);
        Assert.Equal("RADIO 1", radio.Name);
        Assert.Equal("Local Guard", radio.LocalName);
        Assert.Equal(251.000f, radio.Frequency);
        Assert.Equal("new-red", radio.Passcode);
        Assert.Equal(0.45f, radio.Volume);
        Assert.Equal(RadioEar.Left, radio.Ear);
        Assert.Equal(456, radio.HudColorArgb);
        Assert.Equal(33, radio.HudX);
        Assert.Equal(44, radio.HudY);
        Assert.Equal(PttBindingType.Keyboard, radio.PttPrimary.Type);
        Assert.Equal(66, radio.PttPrimary.KeyCode);
        Assert.Equal("B", radio.PttPrimary.DisplayName);
    }
}
