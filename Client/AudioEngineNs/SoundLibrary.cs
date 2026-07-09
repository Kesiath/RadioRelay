using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Client.AudioEngineNs
{
    /// 
    /// Loads the app's recorded sound assets (transmit/receive key clicks,
    /// connect/disconnect beeps, frequency-collision cue, and looping
    /// per-band background static) from embedded WAV resources, decoding
    /// each once into a normalized mono float array at this app's internal
    /// 16kHz pipeline rate so they can be mixed directly alongside voice
    /// audio through the same buffers/panning already used for everything
    /// else.
    /// 
    public static class SoundLibrary
    {
        private const int TargetSampleRate = AudioEngine.SampleRate;

        public static readonly float[] TxStart = Load("tx_start.wav");
        public static readonly float[] TxEnd = Load("tx_end.wav");
        public static readonly float[] RxStart = Load("rx_start.wav");
        public static readonly float[] RxEnd = Load("rx_end.wav");
        public static readonly float[] BeepConnected = Load("beep_connected.wav");
        public static readonly float[] BeepDisconnected = Load("beep_disconnected.wav");
        public static readonly float[] Collision = Load("frq_collision.wav");

        private static readonly float[] HfNoiseLoop = Load("hf_noise.wav");
        private static readonly float[] VhfNoiseLoop = Load("vhf_noise.wav");
        private static readonly float[] UhfNoiseLoop = Load("uhf_noise.wav");

        public static float[] GetBandNoiseLoop(RadioBand band) => band switch
        {
            RadioBand.HF => HfNoiseLoop,
            RadioBand.UHF => UhfNoiseLoop,
            _ => VhfNoiseLoop
        };

        private static float[] Load(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();

            // Match on ".{fileName}" rather than a bare suffix match --
            // MSBuild's default embedded-resource naming joins path
            // segments with '.', so e.g. "hf_noise.wav" as a bare suffix
            // would ALSO match the resource names for "vhf_noise.wav" and
            // "uhf_noise.wav" (both literally end with that substring).
            // Requiring the preceding '.' disambiguates them correctly.
            string suffix = "." + fileName;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                throw new FileNotFoundException(
                    $"Embedded sound resource not found: {fileName}. " +
                    $"Check that Client/Assets/Sounds/{fileName} exists and is included as an " +
                    "EmbeddedResource in RadioRelay.Client.csproj.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new WaveFileReader(stream);

            ISampleProvider sampleProvider = reader.ToSampleProvider();

            if (sampleProvider.WaveFormat.Channels == 2)
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider) { LeftVolume = 0.5f, RightVolume = 0.5f };

            if (sampleProvider.WaveFormat.SampleRate != TargetSampleRate)
                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);

            var samples = new List<float>();
            var buffer = new float[1024];
            int read;
            while ((read = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                    samples.Add(buffer[i]);
            }

            return samples.ToArray();
        }
    }
}
