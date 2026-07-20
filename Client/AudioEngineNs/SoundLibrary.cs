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
    /// <summary>
    /// Loads embedded WAV cues and band noise as normalized 16 kHz mono samples.
    /// </summary>
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

            // Require the separator so hf_noise.wav cannot match VHF or UHF resources.
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
