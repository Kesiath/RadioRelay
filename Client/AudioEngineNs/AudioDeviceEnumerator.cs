using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RadioRelay.Client.AudioEngineNs
{
    /// Lists available microphone/speaker devices via the same
    /// WinMM device indices that WaveInEvent/WaveOutEvent's DeviceNumber
    /// uses, so a device picked here can be passed straight through.
    public static class AudioDeviceEnumerator
    {
        public static List<(int Index, string Name)> GetInputDevices()
        {
            var list = new List<(int, string)> { (-1, "Default microphone") };
            var legacyNames = new List<string>();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                legacyNames.Add(WaveIn.GetCapabilities(i).ProductName);
            var displayNames = ResolveFullNames(legacyNames, GetCoreAudioNames(DataFlow.Capture));
            for (int i = 0; i < displayNames.Count; i++)
                list.Add((i, displayNames[i]));
            return list;
        }

        public static List<(int Index, string Name)> GetOutputDevices()
        {
            var list = new List<(int, string)> { (-1, "Default speakers") };
            var legacyNames = new List<string>();
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                legacyNames.Add(WaveOut.GetCapabilities(i).ProductName);
            var displayNames = ResolveFullNames(legacyNames, GetCoreAudioNames(DataFlow.Render));
            for (int i = 0; i < displayNames.Count; i++)
                list.Add((i, displayNames[i]));
            return list;
        }

        private static List<string> GetCoreAudioNames(DataFlow flow)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return enumerator
                    .EnumerateAudioEndPoints(flow, DeviceState.Active)
                    .Select(device => device.FriendlyName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
            }
            catch
            {
                // Core Audio discovery is an enhancement to the labels only.
                // Keep the working WinMM list if endpoint metadata is unavailable.
                return new List<string>();
            }
        }

        internal static List<string> ResolveFullNames(
            IReadOnlyList<string> legacyNames,
            IReadOnlyList<string> coreAudioNames)
        {
            var resolved = new List<string>(legacyNames.Count);
            var used = new bool[coreAudioNames.Count];

            foreach (var legacyValue in legacyNames)
            {
                var legacy = legacyValue?.Trim() ?? string.Empty;
                var match = FindBestMatch(legacy, coreAudioNames, used);
                if (match >= 0)
                {
                    used[match] = true;
                    resolved.Add(coreAudioNames[match]);
                }
                else
                {
                    resolved.Add(legacy);
                }
            }

            return resolved;
        }

        private static int FindBestMatch(string legacy, IReadOnlyList<string> fullNames, bool[] used)
        {
            if (legacy.Length == 0) return -1;

            for (var i = 0; i < fullNames.Count; i++)
            {
                if (!used[i] && fullNames[i].StartsWith(legacy, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            for (var i = 0; i < fullNames.Count; i++)
            {
                if (!used[i] && fullNames[i].Contains(legacy, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            var qualifierIndex = legacy.IndexOf(" (", StringComparison.Ordinal);
            var stableStem = qualifierIndex >= 8 ? legacy[..qualifierIndex] : legacy;
            if (stableStem.Length < 8) return -1;
            for (var i = 0; i < fullNames.Count; i++)
            {
                if (!used[i] && fullNames[i].Contains(stableStem, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }
    }
}
