using System.Collections.Generic;
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
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                list.Add((i, WaveIn.GetCapabilities(i).ProductName));
            return list;
        }

        public static List<(int Index, string Name)> GetOutputDevices()
        {
            var list = new List<(int, string)> { (-1, "Default speakers") };
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                list.Add((i, WaveOut.GetCapabilities(i).ProductName));
            return list;
        }
    }
}
