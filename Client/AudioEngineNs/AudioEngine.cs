using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RadioRelay.Client.Networking;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;
using RadioRelay.Shared.Protocol;
using RadioRelay.Shared.Security;

namespace RadioRelay.Client.AudioEngineNs
{
    public class CapturedAudioEventArgs : EventArgs
    {
        public AudioPacket Packet = new();
    }

    public sealed class ApplicationAmbienceCaptureFailedEventArgs : EventArgs
    {
        public required string Message { get; init; }
    }

    /// <summary>
    /// Describes a local TX or remote RX lifecycle transition.
    /// </summary>
    public class TransmissionEventArgs : EventArgs
    {
        public required RadioChannel Channel { get; init; }
        public required bool IsLocalTransmit { get; init; }
        public string RemoteCallsign { get; init; } = "";
        public string RemoteClientId { get; init; } = "";
        public long LifecycleSequence { get; init; }
    }

    /// <summary>
    /// Stores mutable state for one keyed radio transmission.
    /// </summary>
    internal class TxState
    {
        public bool IsActive;
        public bool IsNetworkEpochAdvertised;
        public ushort Sequence;
        public int MediaFramesSent;
        public uint TransmissionAudioSeed;
        public ulong TransmissionId;
        public float Frequency;
        public string RadioName = "";
        public string SenderName = "";
        public NetOption Net = NetOption.Unencrypted;
        public RadioBand Band;
        public bool IsIntercom;
        public RadioEar Ear;
        public RadioEffectProfile? Profile;
        public readonly List<short> Accumulator = new();
        public readonly List<float> AmbienceAccumulator = new();
    }

    internal readonly record struct PendingRemoteFrame(
        ushort Sequence,
        byte[] OpusPayload,
        bool IsStart,
        bool IsEnd);

    internal readonly record struct PendingRemoteEnd(
        ushort Sequence,
        byte[] OpusPayload,
        DateTime ExpiresUtc);

    internal sealed class PendingReceiveHandoff
    {
        private const int MaxFrames = 128;
        private readonly HashSet<ushort> _sequences = new();

        public required RadioTransmissionKey Transmission { get; init; }
        public required string Callsign { get; init; }
        public required uint AudioSeed { get; init; }
        public required float ReceiverOffsetMHz { get; init; }
        public List<PendingRemoteFrame> Frames { get; } = new();

        public void Add(PendingRemoteFrame frame)
        {
            if (frame.OpusPayload.Length == 0 || _sequences.Add(frame.Sequence))
            {
                if (Frames.Count < MaxFrames)
                    Frames.Add(frame);
            }
        }
    }

    internal readonly record struct PreparedTransmitFrame(
        EncodedFrame Encoded,
        AudioPacket? NetworkPacket);

    internal readonly record struct CapturedMicrophoneBuffer(
        byte[] Pcm,
        long Generation);

    /// <summary>
    /// Stores receive DSP, carrier arbitration, lifecycle, and HUD state for one channel.
    /// </summary>
    internal class RxState
    {
        public bool IsReceivingActive;
        public RadioNoiseGenerator Noise { get; } = new();
        public ReceiverDetuningEffect Detuning { get; } = new();
        public RadioInterferenceTracker Interference { get; } = new();
        public RadioTalkOverMonitor TalkOver { get; } = new();
        public LoopingAudioCue TalkOverWarningCue { get; } = new(SoundLibrary.Collision);
        public RadioCollisionDestructionModel CollisionDestruction { get; } = new(AudioEngine.SampleRate);
        public RadioBand CachedBand;
        public bool CachedIsIntercom;
        public RadioEffectProfile? CachedProfile;

        // Track HUD lifecycle separately so every start receives one matching end.
        public bool IsReceiveHudActive => ActiveRemoteHudByClient.Count > 0;
        public string ActiveRemoteClientId = "";
        public string ActiveRemoteCallsign = "";
        public readonly Dictionary<string, string> ActiveRemoteHudByClient = new();
        public string PendingRemoteClientId = "";
        public string PendingRemoteCallsign = "";
        public string ActiveAudibleTransmissionHudId = "";
        public uint PendingTransmissionAudioSeed;
        public float PendingReceiverOffsetMHz;
        public bool HasAudibleReceiveInFlight;
        public DateTime? LastRemotePacketUtc;
        public readonly Dictionary<Guid, DateTime> LastRemotePacketUtcByClient = new();
        public readonly Dictionary<RadioTransmissionKey, DateTime> LastRemotePacketUtcByTransmission = new();
        public readonly Dictionary<RadioTransmissionKey, DateTime> RecentlyEndedRemoteTransmissions = new();
        public readonly Dictionary<RadioTransmissionKey, PendingRemoteEnd> PendingEarlyEnds = new();
        public readonly List<PendingReceiveHandoff> PendingHandoffs = new();
        public bool IsAudibleTransmissionTerminalPending;
    }

    /// <summary>
    /// Captures microphone audio, applies radio DSP, Opus, and encryption, then
    /// receives, dejitters, processes, routes, and mixes remote audio.
    /// </summary>
    public class AudioEngine : IDisposable
    {
        public const int SampleRate = 16000;
        private static readonly WaveFormat Format = new(SampleRate, 16, 1);
        private const int FrameSize = OpusCodec.FrameSize; // 20 ms at 16 kHz.
        internal const int MicrophoneCaptureBufferMilliseconds = 20;
        internal const int MicTestPrebufferMilliseconds = 200;
        private const int MicTestBufferDurationMilliseconds = 500;
        internal const int PassthroughOutputSampleRate = 48000;
        internal const int PassthroughOutputLatencyMilliseconds = 20;
        internal const int MainOutputLatencyMilliseconds = 150;
        internal const int MainOutputBufferCount = 3;
        private const int PassthroughBufferDurationMilliseconds = 100;
        internal const int PassthroughMaximumLiveBacklogMilliseconds = 60;
        internal const int MaxQueuedMicrophoneBuffers = 4;
        internal const int TransmissionEndRedundantPacketCount = 3;
        internal const int TransmissionStartRedundantFrameCount = 3;
        internal static readonly TimeSpan StaleRemoteActivityFallbackTimeout = TimeSpan.FromMilliseconds(500);
        internal static readonly TimeSpan LateRemoteMediaGracePeriod = TimeSpan.FromSeconds(2);
        private const int MaxRecentlyEndedRemoteTransmissions = 512;
        private const int MaxPendingReceiveHandoffs = 8;

        private WaveInEvent? _waveIn;
        private WaveOutEvent? _waveOut;
        private WasapiOut? _passthroughWasapiOut;
        private MMDevice? _passthroughDevice;
        private int _inputDeviceIndex;
        private int _outputDeviceIndex;
        private string? _passthroughDeviceId;
        private readonly MixingSampleProvider _mixer; // Stereo output mix.
        private readonly SoftLimiterSampleProvider _outputLimiter;
        private readonly Dictionary<RadioChannel, (BufferedWaveProvider buffer, VolumeSampleProvider vol, PanningSampleProvider pan)> _channelBuffers = new();
        private readonly Dictionary<RadioChannel, JitterBuffer> _jitterBuffers = new();
        private readonly Dictionary<RadioChannel, RxState> _rxState = new();
        private readonly Dictionary<RadioChannel, (BufferedWaveProvider buffer, PanningSampleProvider pan)> _sidetoneBuffers = new();
        private readonly Dictionary<RadioChannel, TxState> _txState = new();
        private readonly Dictionary<RadioChannel, (RadioBand band, bool isIntercom, RadioEffectProfile profile)> _txProfiles = new();
        private readonly BufferedWaveProvider _systemSoundBuffer;
        private readonly BufferedWaveProvider _micTestBuffer;
        private BufferedWaveProvider _passthroughBuffer;
        private readonly ApplicationAmbienceSource _applicationAmbience = new();
        private readonly LocalRadioPassthroughProcessor _passthroughProcessor = new();
        private readonly LocalPassthroughOutputConverter _passthroughOutputConverter = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<CapturedMicrophoneBuffer> _micCaptureQueue = new();
        private int _micCaptureQueueCount;
        private readonly object _micCaptureProducerLock = new();
        private long _micCaptureGeneration;
        private readonly System.Threading.AutoResetEvent _micCaptureSignal = new(false);
        private readonly object _micProcessingGate = new();
        private System.Threading.Thread? _micProcessingThread;
        private volatile bool _micProcessingStopping;
        private bool _disposed;
        private readonly SecureAudioCodec _secureCodec;
        private readonly System.Threading.Timer _jitterTicker;
        private readonly object _stateLock = new();
        private long _transmissionLifecycleSequence;
        private bool _isMicTestActive;
        private bool _networkAdvertisingEnabled;

        public List<RadioChannel> Channels { get; }
        public Guid ClientId { get; }

        /// <summary>
        /// Callsign stamped onto outgoing audio packets.
        /// </summary>
        public string Callsign { get; set; } = "";

        /// <summary>
        /// Linear microphone gain applied before radio DSP and encoding.
        /// </summary>
        public float InputGain { get; set; } = 1.0f;
        public float ApplicationAmbienceGain { get; set; } = 0.38f;
        public float PassthroughVolume { get; set; } = 1.0f;

        public bool IsMicTestActive
        {
            get
            {
                lock (_stateLock)
                    return _isMicTestActive;
            }
        }

        public string? PassthroughDeviceId
        {
            get
            {
                lock (_stateLock)
                    return _passthroughDeviceId;
            }
        }

        /// <summary>
        /// Local PTT key and release cue volume from zero to one.
        /// </summary>
        public float InputClickVolume { get; set; } = 1.0f;

        /// <summary>
        /// Local talk-over warning volume from zero to one.
        /// </summary>
        public float TalkOverWarningVolume { get; set; } = 1.0f;

        /// <summary>
        /// Remote transmission cue volume from zero to one.
        /// </summary>
        public float OutputClickVolume { get; set; } = 1.0f;

        private const float AppVolumeBoost = 1.0f;
        private const float FilteredReceiveStartCueGain = 3.5f;
        private const float FilteredReceiveEndCueGain = 1.4f;

        private static float BoostVolume(float volume)
        {
            return Math.Max(0f, volume * AppVolumeBoost);
        }

        /// <summary>
        /// Raised whenever a fully-encoded (and possibly encrypted) packet is ready to send.
        /// </summary>
        public event EventHandler<CapturedAudioEventArgs>? AudioCaptured;
        public event EventHandler<ApplicationAmbienceCaptureFailedEventArgs>? ApplicationAmbienceCaptureFailed;

        /// <summary>
        /// Raised when local TX or remote RX becomes active or inactive.
        /// </summary>
        public event EventHandler<TransmissionEventArgs>? TransmissionStarted;
        public event EventHandler<TransmissionEventArgs>? TransmissionEnded;

        public AudioEngine(
            List<RadioChannel> channels,
            int micDeviceIndex = -1,
            int speakerDeviceIndex = -1,
            bool startAudioDevices = true,
            Guid clientId = default)
        {
            Channels = channels;
            ClientId = clientId;
            _secureCodec = new SecureAudioCodec(SampleRate);
            _inputDeviceIndex = micDeviceIndex;
            _outputDeviceIndex = speakerDeviceIndex;

            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2))
            {
                ReadFully = true
            };
            _outputLimiter = new SoftLimiterSampleProvider(_mixer);

            // Hold the longest system cue added in one operation.
            _systemSoundBuffer = new BufferedWaveProvider(Format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(10)
            };
            _mixer.AddMixerInput(new PanningSampleProvider(_systemSoundBuffer.ToSampleProvider()) { Pan = 0f });

            // Route the raw mic test directly to the selected output.
            _micTestBuffer = new BufferedWaveProvider(Format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(MicTestBufferDurationMilliseconds)
            };
            _mixer.AddMixerInput(new PanningSampleProvider(_micTestBuffer.ToSampleProvider()) { Pan = 0f });

            _passthroughBuffer = CreatePassthroughBuffer(PassthroughOutputSampleRate);
            _applicationAmbience.CaptureFailed += message => SafeInvoke(
                ApplicationAmbienceCaptureFailed,
                new ApplicationAmbienceCaptureFailedEventArgs { Message = message });

            foreach (var ch in channels)
            {
                AddChannelBuffer(ch);
                AddSidetoneBuffer(ch);
                _jitterBuffers[ch] = new JitterBuffer(new OpusCodec(SampleRate));
                _rxState[ch] = new RxState();
                _txState[ch] = new TxState();
            }

            if (startAudioDevices)
            {
                StartMicProcessingThread();
                _waveOut = CreateMainWaveOut(_outputDeviceIndex);
                _waveOut.Init(_outputLimiter);
                _waveOut.Play();

                _waveIn = new WaveInEvent
                {
                    DeviceNumber = _inputDeviceIndex,
                    WaveFormat = Format,
                    BufferMilliseconds = MicrophoneCaptureBufferMilliseconds
                };
                _waveIn.DataAvailable += OnMicDataAvailable;
                _waveIn.StartRecording();
            }

            // Tick every jitter buffer at the Opus frame cadence.
            _jitterTicker = new System.Threading.Timer(_ => RunBackgroundCallback(TickJitterBuffers), null, 20, 20);
        }

        /// <summary>
        /// Switches the microphone device; -1 selects the system default.
        /// </summary>
        public void SetInputDevice(int deviceIndex)
        {
            lock (_stateLock)
            {
                if (deviceIndex == _inputDeviceIndex) return;
                _inputDeviceIndex = deviceIndex;

                var oldWaveIn = _waveIn;
                if (oldWaveIn == null) return;
                oldWaveIn.DataAvailable -= OnMicDataAvailable;
                oldWaveIn.StopRecording();
                oldWaveIn.Dispose();

                _waveIn = new WaveInEvent
                {
                    DeviceNumber = _inputDeviceIndex,
                    WaveFormat = Format,
                    BufferMilliseconds = MicrophoneCaptureBufferMilliseconds
                };
                _waveIn.DataAvailable += OnMicDataAvailable;
                _waveIn.StartRecording();

            }
        }

        /// <summary>
        /// Switches the speaker device; -1 selects the system default.
        /// </summary>
        public void SetOutputDevice(int deviceIndex)
        {
            lock (_stateLock)
            {
                if (deviceIndex == _outputDeviceIndex) return;
                _outputDeviceIndex = deviceIndex;

                var oldWaveOut = _waveOut;
                if (oldWaveOut == null) return;
                oldWaveOut.Stop();
                oldWaveOut.Dispose();

                _waveOut = CreateMainWaveOut(_outputDeviceIndex);
                _waveOut.Init(_outputLimiter);
                _waveOut.Play();

            }
        }

        public void SetMicTestActive(bool active)
        {
            lock (_stateLock)
            {
                if (_isMicTestActive == active) return;
                _isMicTestActive = active;
                lock (_micTestBuffer)
                {
                    _micTestBuffer.ClearBuffer();
                    if (active)
                    {
                        // Prime the monitor to prevent alternating audio and underflow.
                        var prebuffer = new byte[SampleRate * 2 * MicTestPrebufferMilliseconds / 1000];
                        _micTestBuffer.AddSamples(prebuffer, 0, prebuffer.Length);
                    }
                }
            }
        }

        /// <summary>
        /// Selects a local application whose output is mixed quietly into active transmissions.
        /// Null disables application ambience.
        /// </summary>
        public void SetApplicationAmbienceTarget(ApplicationAudioTarget? target) =>
            _applicationAmbience.SetTarget(target);

        /// <summary>
        /// Selects the playback endpoint feeding a virtual audio cable.
        /// Null disables passthrough and is the persisted/default state.
        /// </summary>
        public void SetPassthroughDevice(string? deviceId)
        {
            lock (_stateLock)
            {
                if (string.Equals(deviceId, _passthroughDeviceId, StringComparison.Ordinal) &&
                    (deviceId == null || _passthroughWasapiOut != null))
                {
                    return;
                }

                _passthroughWasapiOut?.Stop();
                _passthroughWasapiOut?.Dispose();
                _passthroughWasapiOut = null;
                _passthroughDevice?.Dispose();
                _passthroughDevice = null;
                _passthroughDeviceId = null;
                _passthroughProcessor.Reset();
                _passthroughOutputConverter.Reset();
                lock (_passthroughBuffer)
                    _passthroughBuffer.ClearBuffer();

                if (deviceId == null) return;

                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                int outputSampleRate = device.AudioClient.MixFormat.SampleRate;
                _passthroughOutputConverter.SetOutputSampleRate(outputSampleRate);
                _passthroughBuffer = CreatePassthroughBuffer(outputSampleRate);
                var output = new WasapiOut(
                    device,
                    AudioClientShareMode.Shared,
                    useEventSync: true,
                    latency: PassthroughOutputLatencyMilliseconds);
                try
                {
                    output.Init(_passthroughBuffer);
                    output.Play();
                    _passthroughDevice = device;
                    _passthroughWasapiOut = output;
                    _passthroughDeviceId = deviceId;
                    if (_txState.Values.Any(state => state.IsActive))
                        ResetPassthroughBufferForTransmission();
                }
                catch
                {
                    output.Dispose();
                    device.Dispose();
                    throw;
                }
            }
        }

        private void AddChannelBuffer(RadioChannel channel)
        {
            var buffer = new BufferedWaveProvider(Format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            var vol = new VolumeSampleProvider(buffer.ToSampleProvider()) { Volume = BoostVolume(channel.Volume) };
            var pan = new PanningSampleProvider(vol) { Pan = EarToPan(channel.Ear) };
            _channelBuffers[channel] = (buffer, vol, pan);
            _mixer.AddMixerInput(pan);
        }

        private void AddSidetoneBuffer(RadioChannel channel)
        {
            // TX cues bypass receive volume but follow the radio's output channel.
            var buffer = new BufferedWaveProvider(Format) { DiscardOnBufferOverflow = true };
            var pan = new PanningSampleProvider(buffer.ToSampleProvider()) { Pan = EarToPan(channel.Ear) };
            _sidetoneBuffers[channel] = (buffer, pan);
            _mixer.AddMixerInput(pan);
        }

        private static float EarToPan(RadioEar ear) => ear switch
        {
            RadioEar.Left => -1f,
            RadioEar.Right => 1f,
            _ => 0f
        };

        private static WaveOutEvent CreateMainWaveOut(int deviceIndex) => new()
        {
            DeviceNumber = deviceIndex,
            DesiredLatency = MainOutputLatencyMilliseconds,
            NumberOfBuffers = MainOutputBufferCount
        };

        private static BufferedWaveProvider CreatePassthroughBuffer(int sampleRate) =>
            new(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
                BufferDuration = TimeSpan.FromMilliseconds(PassthroughBufferDurationMilliseconds)
            };

        /// <summary>
        /// Applies a radio output-channel change immediately.
        /// </summary>
        public void UpdateChannelEar(RadioChannel channel)
        {
            lock (_stateLock)
            {
                if (_channelBuffers.TryGetValue(channel, out var entry))
                    entry.pan.Pan = EarToPan(channel.Ear);
                if (_sidetoneBuffers.TryGetValue(channel, out var sidetone))
                    sidetone.pan.Pan = EarToPan(channel.Ear);

            }
        }

        /// <summary>
        /// Applies a listen-volume change immediately. Zero is a true receiver-off
        /// state: queued audio and receive lifecycle/HUD state are discarded.
        /// </summary>
        public void UpdateChannelVolume(RadioChannel channel)
        {
            bool stopTransmitting = false;
            lock (_stateLock)
            {
                if (_channelBuffers.TryGetValue(channel, out var entry))
                    entry.vol.Volume = BoostVolume(channel.Volume);

                if (RadioReceiveMute.IsReceiveDisabled(channel.Volume) &&
                    _rxState.TryGetValue(channel, out var rxState))
                {
                    DisableChannelReceive(channel, rxState);
                }

                if (RadioReceiveMute.IsReceiveDisabled(channel.Volume) &&
                    _txState.TryGetValue(channel, out var txState) &&
                    txState.IsActive)
                {
                    stopTransmitting = true;
                }
            }

            // Preserve microphone-gate then state-lock ordering.
            if (stopTransmitting)
                SetTransmitting(channel, false);
        }

        /// <summary>
        /// Applies tuning changes as an RF boundary and restarts any active TX epoch.
        /// </summary>
        public void OnChannelTuningChanged(RadioChannel channel)
        {
            lock (_micProcessingGate)
            {
                long completedCaptureGeneration = AdvanceMicCaptureGeneration();
                DrainMicCaptureQueue(completedCaptureGeneration);
                lock (_stateLock)
                {
                    _txProfiles.Remove(channel);
                    if (_rxState.TryGetValue(channel, out var rxState))
                    {
                        DisableChannelReceive(channel, rxState);
                        rxState.CachedProfile = null;
                        rxState.Noise.Reset();
                    }

                    if (!_txState.TryGetValue(channel, out var state) || !state.IsActive)
                        return;

                    bool wasAdvertised = state.IsNetworkEpochAdvertised;
                    FlushPartialTransmitFrameLocked(channel, state, wasAdvertised);
                    if (wasAdvertised)
                        EmitTransmissionEndsLocked(state);
                    _secureCodec.EndTransmitStream(state.TransmissionId);
                    BeginTransmitEpochLocked(channel, state, wasAdvertised);
                }
            }
        }

        /// <summary>
        /// Ends all advertised PTT epochs and suppresses network audio until restart.
        /// </summary>
        public void SendActiveTransmissionEnds()
        {
            lock (_micProcessingGate)
            {
                long completedCaptureGeneration = AdvanceMicCaptureGeneration();
                DrainMicCaptureQueue(completedCaptureGeneration);
                lock (_stateLock)
                {
                    _networkAdvertisingEnabled = false;
                    foreach (var entry in _txState)
                    {
                        var state = entry.Value;
                        if (!state.IsActive || !state.IsNetworkEpochAdvertised) continue;

                        FlushPartialTransmitFrameLocked(entry.Key, state, emitNetwork: true);
                        EmitTransmissionEndsLocked(state);
                        state.IsNetworkEpochAdvertised = false;
                    }
                }
            }
        }

        /// <summary>
        /// Restarts held PTTs with fresh epochs after network recovery.
        /// </summary>
        public void RestartActiveTransmissionStreams()
        {
            lock (_micProcessingGate)
            {
                long completedCaptureGeneration = AdvanceMicCaptureGeneration();
                DrainMicCaptureQueue(completedCaptureGeneration);
                lock (_stateLock)
                {
                    _networkAdvertisingEnabled = true;
                    foreach (var entry in _txState)
                    {
                        var state = entry.Value;
                        if (!state.IsActive) continue;

                        FlushPartialTransmitFrameLocked(entry.Key, state, emitNetwork: false);
                        _secureCodec.EndTransmitStream(state.TransmissionId);
                        BeginTransmitEpochLocked(entry.Key, state, advertiseOnNetwork: true);
                    }
                }
            }
        }

        private void DisableChannelReceive(RadioChannel channel, RxState rxState)
        {
            if (_jitterBuffers.TryGetValue(channel, out var jitter)) jitter.Reset();
            if (_channelBuffers.TryGetValue(channel, out var receiveEntry)) receiveEntry.buffer.ClearBuffer();

            rxState.IsReceivingActive = false;
            rxState.PendingRemoteClientId = "";
            rxState.PendingRemoteCallsign = "";
            rxState.ActiveAudibleTransmissionHudId = "";
            rxState.PendingTransmissionAudioSeed = 0;
            rxState.PendingReceiverOffsetMHz = 0f;
            rxState.HasAudibleReceiveInFlight = false;
            rxState.LastRemotePacketUtc = null;
            rxState.LastRemotePacketUtcByClient.Clear();
            rxState.LastRemotePacketUtcByTransmission.Clear();
            rxState.RecentlyEndedRemoteTransmissions.Clear();
            rxState.PendingEarlyEnds.Clear();
            rxState.PendingHandoffs.Clear();
            rxState.IsAudibleTransmissionTerminalPending = false;
            rxState.TalkOver.ClearRemoteTransmitters();
            rxState.TalkOverWarningCue.Reset();
            rxState.Interference.Reset();
            rxState.CollisionDestruction.Reset();
            EndAllRemoteReceiveHuds(channel, rxState, (_, remoteCallsign, remoteClientId) =>
                RaiseRemoteTransmissionEnded(channel, remoteCallsign, remoteClientId));
        }

        /// <summary>
        /// Gets or rebuilds the channel's band-specific effect profile.
        /// </summary>
        private RadioEffectProfile GetTxProfile(RadioChannel channel)
        {
            var band = RadioBandExtensions.FromFrequencyMHz(channel.Frequency);
            if (_txProfiles.TryGetValue(channel, out var cached) &&
                cached.band == band &&
                cached.isIntercom == channel.IsIntercom)
                return cached.profile;

            var profile = RadioEffectProfile.ForBand(band, channel.IsIntercom, SampleRate);
            _txProfiles[channel] = (band, channel.IsIntercom, profile);
            return profile;
        }

        private RadioEffectProfile GetRxProfile(RadioChannel channel, RxState state)
        {
            var band = RadioBandExtensions.FromFrequencyMHz(channel.Frequency);
            if (state.CachedProfile != null &&
                state.CachedBand == band &&
                state.CachedIsIntercom == channel.IsIntercom)
                return state.CachedProfile;

            var profile = RadioEffectProfile.ForBand(band, channel.IsIntercom, SampleRate);
            state.CachedBand = band;
            state.CachedIsIntercom = channel.IsIntercom;
            state.CachedProfile = profile;
            return profile;
        }

        /// <summary>
        /// Changes PTT state for one radio without affecting other channels.
        /// </summary>
        public void SetTransmitting(RadioChannel channel, bool active)
        {
            if (!active)
            {
                // Drain captured work before queuing end cues and controls.
                lock (_micProcessingGate)
                {
                    long completedCaptureGeneration = AdvanceMicCaptureGeneration();
                    DrainMicCaptureQueue(completedCaptureGeneration);
                    lock (_stateLock)
                        SetTransmittingLocked(channel, false);
                }
                return;
            }

            // Exclude capture blocks queued before key-down.
            lock (_micProcessingGate)
            {
                long completedCaptureGeneration = AdvanceMicCaptureGeneration();
                bool hadActiveTransmitter;
                lock (_stateLock)
                    hadActiveTransmitter = _txState.Values.Any(tx => tx.IsActive);

                // Preserve captured speech when another radio keys simultaneously.
                if (hadActiveTransmitter)
                    DrainMicCaptureQueue(completedCaptureGeneration);
                else
                    DiscardQueuedMicrophoneBuffers(completedCaptureGeneration);

                lock (_stateLock)
                    SetTransmittingLocked(channel, true);
            }
        }

        private void SetTransmittingLocked(RadioChannel channel, bool active)
        {
            if (!_txState.TryGetValue(channel, out var state)) return;
            if (active && !RadioReceiveMute.CanStartTransmission(channel.Volume)) return;
            if (active == state.IsActive) return;

            if (active)
            {
                if (!_txState.Values.Any(tx => tx.IsActive))
                {
                    _applicationAmbience.ResetTransmissionBuffer();
                    _passthroughProcessor.BeginTransmission();
                }
                state.IsActive = true;
                BeginTransmitEpochLocked(channel, state);
            }
            else
            {
                // Preserve the final partial frame at PTT-up.
                FlushPartialTransmitFrameLocked(channel, state, state.IsNetworkEpochAdvertised);
                if (state.IsNetworkEpochAdvertised)
                    EmitTransmissionEndsLocked(state);
                _secureCodec.EndTransmitStream(state.TransmissionId);
                state.IsNetworkEpochAdvertised = false;
                state.IsActive = false;

                if (!_txState.Values.Any(tx => tx.IsActive))
                {
                    QueuePassthroughPcm(_passthroughProcessor.EndTransmission());
                    _passthroughProcessor.Reset();
                    _passthroughOutputConverter.Reset();
                }
            }

            if (_rxState.TryGetValue(channel, out var rxState))
            {
                rxState.TalkOver.SetLocalTransmitting(active);
                if (active)
                {
                    // Mute receive audio during TX while retaining remote HUD activity.
                    if (_jitterBuffers.TryGetValue(channel, out var jitter))
                        jitter.Reset();
                    if (_channelBuffers.TryGetValue(channel, out var receiveEntry))
                        receiveEntry.buffer.ClearBuffer();
                    rxState.IsReceivingActive = false;
                    rxState.PendingRemoteClientId = "";
                    rxState.PendingRemoteCallsign = "";
                    rxState.ActiveAudibleTransmissionHudId = "";
                    rxState.HasAudibleReceiveInFlight = false;
                    rxState.PendingHandoffs.Clear();
                    rxState.IsAudibleTransmissionTerminalPending = false;
                    rxState.CollisionDestruction.Reset();
                }
                else
                {
                    if (_sidetoneBuffers.TryGetValue(channel, out var sidetone))
                        ClearLocalTalkOverWarning(rxState, sidetone.buffer);
                    else
                        rxState.TalkOverWarningCue.Reset();
                }
            }

            if (!active)
            {
                state.Accumulator.Clear();
                state.AmbienceAccumulator.Clear();
                PlayLocalSidetone(channel, SoundLibrary.TxEnd);
                RaiseTransmissionEnded(new TransmissionEventArgs { Channel = channel, IsLocalTransmit = true, LifecycleSequence = NextTransmissionLifecycleSequence() });
            }
            else
            {
                PlayLocalSidetone(channel, SoundLibrary.TxStart);
                RaiseTransmissionStarted(new TransmissionEventArgs { Channel = channel, IsLocalTransmit = true, RemoteCallsign = state.SenderName, LifecycleSequence = NextTransmissionLifecycleSequence() });
            }
        }

        private void BeginTransmitEpochLocked(
            RadioChannel channel,
            TxState state,
            bool? advertiseOnNetwork = null)
        {
            state.Sequence = 0;
            state.MediaFramesSent = 0;
            state.TransmissionAudioSeed = unchecked((uint)Random.Shared.Next(1, int.MaxValue));
            state.TransmissionId = CreateTransmissionId();
            state.IsNetworkEpochAdvertised = advertiseOnNetwork ?? _networkAdvertisingEnabled;
            state.Frequency = channel.Frequency;
            state.RadioName = channel.Name;
            state.SenderName = Callsign;
            state.Net = channel.SelectedNet;
            state.Band = RadioBandExtensions.FromFrequencyMHz(channel.Frequency);
            state.IsIntercom = channel.IsIntercom;
            state.Ear = channel.Ear;
            state.Profile = GetTxProfile(channel);
            state.Accumulator.Clear();
            state.AmbienceAccumulator.Clear();
            state.Profile.ResetTransmit(state.TransmissionAudioSeed);
            _secureCodec.BeginTransmitStream(state.TransmissionId);
            _passthroughProcessor.ResetChannel(channel);
        }

        private static ulong CreateTransmissionId()
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            ulong value;
            do
            {
                RandomNumberGenerator.Fill(bytes);
                value = BitConverter.ToUInt64(bytes);
            }
            while (value == 0);

            return value;
        }

        private void FlushPartialTransmitFrameLocked(
            RadioChannel channel,
            TxState state,
            bool emitNetwork)
        {
            if (state.Accumulator.Count == 0) return;

            var frame = new short[FrameSize];
            int count = Math.Min(FrameSize, state.Accumulator.Count);
            state.Accumulator.CopyTo(0, frame, 0, count);
            state.Accumulator.Clear();
            var ambience = new float[FrameSize];
            int ambienceCount = Math.Min(count, state.AmbienceAccumulator.Count);
            state.AmbienceAccumulator.CopyTo(0, ambience, 0, ambienceCount);
            state.AmbienceAccumulator.Clear();
            var prepared = SendFrame(channel, state, frame, ambience, emitNetwork);
            QueuePassthroughFrames(new[] { CreatePassthroughFrame(channel, state, prepared.Encoded) });
            if (prepared.NetworkPacket != null)
                RaiseAudioCaptured(new CapturedAudioEventArgs { Packet = prepared.NetworkPacket });
        }

        private void EmitTransmissionEndsLocked(TxState state)
        {
            foreach (var packet in CreateTransmissionEndPackets(
                state.Frequency,
                state.RadioName,
                state.Net,
                state.Sequence,
                state.SenderName,
                ClientId,
                state.TransmissionId,
                state.TransmissionAudioSeed))
            {
                RaiseAudioCaptured(new CapturedAudioEventArgs { Packet = packet });
            }
        }

        public bool IsTransmitting(RadioChannel channel)
        {
            lock (_stateLock)
            {
                return _txState.TryGetValue(channel, out var s) && s.IsActive;
            }
        }

        private long NextTransmissionLifecycleSequence() =>
            System.Threading.Interlocked.Increment(ref _transmissionLifecycleSequence);

        private static void RunBackgroundCallback(Action action)
        {
            try { action(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch { }
        }

        private void RaiseAudioCaptured(CapturedAudioEventArgs args) =>
            SafeInvoke(AudioCaptured, args);

        private void RaiseTransmissionStarted(TransmissionEventArgs args) =>
            SafeInvoke(TransmissionStarted, args);

        private void RaiseTransmissionEnded(TransmissionEventArgs args) =>
            SafeInvoke(TransmissionEnded, args);

        private void SafeInvoke<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs args)
            where TEventArgs : EventArgs
        {
            if (handlers == null) return;

            foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
            {
                try { handler(this, args); }
                catch
                {
                    // Isolate audio callbacks and timers from subscriber failures.
                }
            }
        }

        internal static IReadOnlyList<AudioPacket> CreateTransmissionEndPackets(
            RadioChannel channel,
            ushort sequence,
            string callsign) =>
            CreateTransmissionEndPackets(
                channel.Frequency,
                channel.Name,
                channel.SelectedNet,
                sequence,
                callsign,
                Guid.Empty,
                0,
                0);

        internal static IReadOnlyList<AudioPacket> CreateTransmissionEndPackets(
            float frequency,
            string radioName,
            NetOption net,
            ushort sequence,
            string callsign,
            Guid clientId,
            ulong transmissionId,
            uint transmissionAudioSeed)
        {
            var packets = new List<AudioPacket>(TransmissionEndRedundantPacketCount);
            for (int i = 0; i < TransmissionEndRedundantPacketCount; i++)
            {
                var packet = new AudioPacket
                {
                    ClientId = clientId,
                    Frequency = frequency,
                    Sequence = sequence,
                    IsTransmissionStart = false,
                    IsTransmissionEnd = true,
                    IsEncrypted = !net.IsUnencrypted,
                    NetIdHash = net.NetIdHash,
                    Nonce = !net.IsUnencrypted ? SecureAudioCodec.CreateModernControlNonce() : null,
                    Tag = !net.IsUnencrypted ? new byte[16] : null,
                    SenderName = callsign,
                    RadioName = radioName,
                    TransmissionAudioSeed = transmissionAudioSeed,
                    TransmissionId = transmissionId,
                    Payload = Array.Empty<byte>()
                };

                if (!net.IsUnencrypted && transmissionId != 0)
                {
                    packet.HeaderAuthTag = PacketCrypto.ComputeHeaderAuthenticationTag(
                        net.Key!,
                        packet.GetAuthenticatedHeaderBytes());
                }

                packets.Add(packet);
            }

            return packets;
        }

        private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
        {
            // Defer DSP so the capture callback can immediately requeue its buffer.
            lock (_micCaptureProducerLock)
            {
                if (_micProcessingStopping) return;

                var capturedPcm = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, capturedPcm, 0, capturedPcm.Length);
                _micCaptureQueue.Enqueue(new CapturedMicrophoneBuffer(
                    capturedPcm,
                    _micCaptureGeneration));
                System.Threading.Interlocked.Increment(ref _micCaptureQueueCount);

                // Drop oldest capture blocks when processing stalls to bound latency.
                while (System.Threading.Volatile.Read(ref _micCaptureQueueCount) > MaxQueuedMicrophoneBuffers &&
                       _micCaptureQueue.TryDequeue(out _))
                {
                    System.Threading.Interlocked.Decrement(ref _micCaptureQueueCount);
                }
            }

            _micCaptureSignal.Set();
        }

        private void StartMicProcessingThread()
        {
            if (_micProcessingThread != null) return;
            _micProcessingStopping = false;
            _micProcessingThread = new System.Threading.Thread(ProcessMicCaptureQueue)
            {
                IsBackground = true,
                Name = "RadioRelay microphone processor",
                Priority = System.Threading.ThreadPriority.AboveNormal
            };
            _micProcessingThread.Start();
        }

        private void ProcessMicCaptureQueue()
        {
            while (!_micProcessingStopping)
            {
                _micCaptureSignal.WaitOne();
                lock (_micProcessingGate)
                    DrainMicCaptureQueue();
            }
        }

        private void DrainMicCaptureQueue(long maximumGeneration = long.MaxValue)
        {
            while (!_micProcessingStopping &&
                   _micCaptureQueue.TryPeek(out var next) &&
                   next.Generation <= maximumGeneration &&
                   _micCaptureQueue.TryDequeue(out var captured))
            {
                System.Threading.Interlocked.Decrement(ref _micCaptureQueueCount);
                RunBackgroundCallback(() => ProcessMicDataAvailable(captured.Pcm));
            }
        }

        private void DiscardQueuedMicrophoneBuffers(long maximumGeneration = long.MaxValue)
        {
            while (_micCaptureQueue.TryPeek(out var next) &&
                   next.Generation <= maximumGeneration &&
                   _micCaptureQueue.TryDequeue(out _))
                System.Threading.Interlocked.Decrement(ref _micCaptureQueueCount);
            if (System.Threading.Volatile.Read(ref _micCaptureQueueCount) < 0)
                System.Threading.Interlocked.Exchange(ref _micCaptureQueueCount, 0);
        }

        private long AdvanceMicCaptureGeneration()
        {
            lock (_micCaptureProducerLock)
            {
                long completedGeneration = _micCaptureGeneration;
                _micCaptureGeneration = unchecked(_micCaptureGeneration + 1);
                return completedGeneration;
            }
        }

        private void ProcessMicDataAvailable(byte[] capturedPcm)
        {
            lock (_stateLock)
            {
                int sampleCount = capturedPcm.Length / 2;

                if (_isMicTestActive)
                {
                    var monitorPcm = CreateMicTestPcm(capturedPcm, capturedPcm.Length, InputGain);
                    lock (_micTestBuffer)
                    {
                        // Preserve the primed queue; overflow handling bounds stalled output.
                        _micTestBuffer.AddSamples(monitorPcm, 0, monitorPcm.Length);
                    }
                }

                List<List<LocalRadioPassthroughFrame>>? passthroughFrames =
                    _passthroughWasapiOut == null ? null : new();
                var networkPackets = new List<AudioPacket>();
                bool hasActiveTransmission = _txState.Values.Any(state => state.IsActive);
                float[] ambienceSamples = hasActiveTransmission
                    ? _applicationAmbience.ReadSamples(sampleCount)
                    : Array.Empty<float>();

                foreach (var kvp in _txState)
                {
                    var channel = kvp.Key;
                    var state = kvp.Value;
                    if (!state.IsActive) continue;

                    for (int i = 0; i < sampleCount; i++)
                    {
                        state.Accumulator.Add(BitConverter.ToInt16(capturedPcm, i * 2));
                        state.AmbienceAccumulator.Add(ambienceSamples[i]);
                    }

                    // Accumulate capture blocks into exact 20 ms Opus frames.
                    int frameOrdinal = 0;
                    while (state.Accumulator.Count >= FrameSize)
                    {
                        var frame = state.Accumulator.GetRange(0, FrameSize).ToArray();
                        state.Accumulator.RemoveRange(0, FrameSize);
                        var ambience = state.AmbienceAccumulator.GetRange(0, FrameSize).ToArray();
                        state.AmbienceAccumulator.RemoveRange(0, FrameSize);
                        var prepared = SendFrame(channel, state, frame, ambience);

                        if (passthroughFrames != null)
                        {
                            while (passthroughFrames.Count <= frameOrdinal)
                                passthroughFrames.Add(new List<LocalRadioPassthroughFrame>());
                            passthroughFrames[frameOrdinal].Add(
                                CreatePassthroughFrame(channel, state, prepared.Encoded));
                        }
                        if (prepared.NetworkPacket != null)
                            networkPackets.Add(prepared.NetworkPacket);
                        frameOrdinal++;
                    }
                }

                if (passthroughFrames != null)
                {
                    foreach (var encodedFrames in passthroughFrames)
                        QueuePassthroughFrames(encodedFrames);
                }

                // Queue local recording before invoking transport subscribers.
                foreach (var packet in networkPackets)
                    RaiseAudioCaptured(new CapturedAudioEventArgs { Packet = packet });

            }
        }

        private PreparedTransmitFrame SendFrame(
            RadioChannel txChannel,
            TxState state,
            short[] frame,
            float[]? ambience = null,
            bool emitNetwork = true)
        {
            var floatSamples = PrepareTransmitSamples(
                frame,
                ambience,
                InputGain,
                ApplicationAmbienceGain);

            var profile = state.Profile ?? throw new InvalidOperationException("Transmit epoch has no DSP profile.");
            var net = state.Net;

            // Run secure-voice coloration through the same FM modulation as speech.
            ApplyTransmitEffects(floatSamples, profile, encrypted: !net.IsUnencrypted);

            for (int i = 0; i < FrameSize; i++)
                frame[i] = (short)Math.Clamp(floatSamples[i] * 32767f, short.MinValue, short.MaxValue);

            var encoded = _secureCodec.EncodeAndEncrypt(frame, net, state.TransmissionId);

            state.Sequence = unchecked((ushort)(state.Sequence + 1));
            state.MediaFramesSent++;

            var packet = new AudioPacket
            {
                ClientId = ClientId,
                Frequency = state.Frequency,
                Sequence = state.Sequence,
                // Set the core Start bit only on frame one to avoid jitter-buffer resets.
                IsTransmissionStart = state.MediaFramesSent == 1,
                IsTransmissionStartHint = state.MediaFramesSent <= TransmissionStartRedundantFrameCount,
                IsTransmissionEnd = false,
                IsEncrypted = encoded.IsEncrypted,
                NetIdHash = encoded.NetIdHash,
                Nonce = encoded.Nonce,
                Tag = encoded.Tag,
                SenderName = state.SenderName,
                RadioName = state.RadioName,
                TransmissionAudioSeed = state.TransmissionAudioSeed,
                TransmissionId = state.TransmissionId,
                Payload = encoded.Payload
            };

            if (!net.IsUnencrypted)
            {
                packet.HeaderAuthTag = PacketCrypto.ComputeHeaderAuthenticationTag(
                    net.Key!,
                    packet.GetAuthenticatedHeaderBytes());
            }

            return new PreparedTransmitFrame(
                encoded,
                emitNetwork && state.IsNetworkEpochAdvertised ? packet : null);
        }

        internal static float[] PrepareTransmitSamples(
            short[] microphoneFrame,
            float[]? ambienceFrame,
            float inputGain,
            float ambienceGain = 1f)
        {
            if (microphoneFrame.Length != FrameSize)
                throw new ArgumentException($"Expected {FrameSize} microphone samples.", nameof(microphoneFrame));
            if (ambienceFrame != null && ambienceFrame.Length != FrameSize)
                throw new ArgumentException($"Expected {FrameSize} ambience samples.", nameof(ambienceFrame));

            var output = new float[FrameSize];
            for (int index = 0; index < FrameSize; index++)
            {
                float microphone = ApplyInputGainSoftClip(microphoneFrame[index] / 32768f, inputGain);
                float ambienceSample = ApplyInputGainSoftClip(
                    ambienceFrame?[index] ?? 0f,
                    Math.Clamp(ambienceGain, 0f, 1f));
                output[index] = Math.Clamp(microphone + ambienceSample, -1f, 1f);
            }

            return output;
        }

        internal static void ApplyTransmitEffects(
            float[] samples,
            RadioEffectProfile profile,
            bool encrypted)
        {
            if (encrypted)
                profile.EncryptionEffect.Process(samples);
            profile.TxEffect.Process(samples);
        }

        internal static float ApplyInputGainSoftClip(float sample, float inputGain)
        {
            float value = sample * Math.Max(0f, inputGain);
            float magnitude = Math.Abs(value);
            const float knee = 0.82f;
            if (magnitude <= knee) return value;

            float compressed = knee + (1f - knee) *
                (1f - MathF.Exp(-(magnitude - knee) / (1f - knee)));
            return MathF.CopySign(Math.Min(compressed, 1f), value);
        }

        private static LocalRadioPassthroughFrame CreatePassthroughFrame(
            RadioChannel channel,
            TxState state,
            EncodedFrame encoded) =>
            new(
                channel,
                encoded.OpusPayload,
                state.TransmissionAudioSeed,
                state.TransmissionId,
                state.Frequency,
                state.IsIntercom,
                state.Ear);

        private void QueuePassthroughFrames(IReadOnlyList<LocalRadioPassthroughFrame> encodedFrames)
        {
            if (_passthroughWasapiOut == null || encodedFrames.Count == 0) return;

            var receivedStereo = _passthroughProcessor.Process(encodedFrames);
            QueuePassthroughPcm(receivedStereo);
        }

        private void QueuePassthroughPcm(short[] receivedStereo)
        {
            if (_passthroughWasapiOut == null || receivedStereo.Length == 0) return;

            var passthroughPcm = _passthroughOutputConverter.Convert(receivedStereo);
            ApplyPassthroughVolume(passthroughPcm, PassthroughVolume);
            lock (_passthroughBuffer)
            {
                TrimPassthroughLiveBacklog(passthroughPcm.Length);
                _passthroughBuffer.AddSamples(passthroughPcm, 0, passthroughPcm.Length);
            }
        }

        internal static void ApplyPassthroughVolume(byte[] floatPcm, float volume)
        {
            if ((floatPcm.Length & (sizeof(float) - 1)) != 0)
                throw new ArgumentException("Expected 32-bit float PCM.", nameof(floatPcm));

            float gain = Math.Clamp(volume, 0f, RadioChannel.MaxReceiveVolume);
            if (Math.Abs(gain - 1f) < 0.0001f) return;

            for (int offset = 0; offset < floatPcm.Length; offset += sizeof(float))
            {
                float sample = BitConverter.ToSingle(floatPcm, offset) * gain;
                if (gain > 1f) sample = SoftLimiterSampleProvider.Limit(sample);
                BitConverter.TryWriteBytes(floatPcm.AsSpan(offset, sizeof(float)), sample);
            }
        }

        private void TrimPassthroughLiveBacklog(int incomingBytes)
        {
            int blockAlign = _passthroughBuffer.WaveFormat.BlockAlign;
            int maximumBytes = _passthroughBuffer.WaveFormat.AverageBytesPerSecond *
                PassthroughMaximumLiveBacklogMilliseconds / 1000;
            maximumBytes -= maximumBytes % blockAlign;

            int allowedExistingBytes = Math.Max(0, maximumBytes - incomingBytes);
            int excessBytes = Math.Max(0, _passthroughBuffer.BufferedBytes - allowedExistingBytes);
            excessBytes -= excessBytes % blockAlign;
            if (excessBytes == 0) return;

            // Drop oldest rendered samples to bound clock drift and output stalls.
            var discard = new byte[excessBytes];
            _passthroughBuffer.Read(discard, 0, discard.Length);
        }

        internal static byte[] CreateMicTestPcm(byte[] capturedPcm, int bytesRecorded, float inputGain)
        {
            int safeByteCount = Math.Clamp(bytesRecorded, 0, capturedPcm.Length) & ~1;
            var monitoredPcm = new byte[safeByteCount];
            for (int offset = 0; offset < safeByteCount; offset += 2)
            {
                short sample = unchecked((short)(capturedPcm[offset] | (capturedPcm[offset + 1] << 8)));
                short scaled = (short)Math.Clamp(
                    Math.Round(sample * Math.Max(0f, inputGain)),
                    short.MinValue,
                    short.MaxValue);
                monitoredPcm[offset] = unchecked((byte)scaled);
                monitoredPcm[offset + 1] = unchecked((byte)(scaled >> 8));
            }

            return monitoredPcm;
        }

        private void ResetPassthroughBufferForTransmission()
        {
            if (_passthroughWasapiOut == null) return;

            lock (_passthroughBuffer)
                _passthroughBuffer.ClearBuffer();
        }

        /// <summary>
        /// Called by the networking layer when an audio packet arrives from another client.
        /// </summary>
        public void OnAudioReceived(AudioPacket packet)
        {
            lock (_stateLock)
            {
                const float tolerance = 0.005f;

                foreach (var ch in Channels)
                {
                    if (Math.Abs(ch.Frequency - packet.Frequency) > tolerance) continue;
                    if (!_channelBuffers.TryGetValue(ch, out var entry)) continue;
                    if (!_jitterBuffers.TryGetValue(ch, out var jitter)) continue;
                    if (!_rxState.TryGetValue(ch, out var rxState)) continue;

                    if (RadioReceiveMute.IsReceiveDisabled(ch.Volume))
                    {
                        DisableChannelReceive(ch, rxState);
                        continue;
                    }
                    var net = ch.SelectedNet;
                    bool netMatches = net.IsUnencrypted
                        ? !packet.IsEncrypted
                        : packet.IsEncrypted && NetIdHashesEqual(net.NetIdHash, packet.NetIdHash);
                    byte[]? opusFrame = null;

                    if (netMatches && !net.IsUnencrypted)
                    {
                        // Authenticate modern metadata before mutating receive state.
                        bool requiresAuthenticatedHeader = RequiresAuthenticatedHeader(packet);
                        if ((requiresAuthenticatedHeader || packet.HeaderAuthTag != null) &&
                            !PacketCrypto.VerifyHeaderAuthenticationTag(
                                net.Key!,
                                packet.GetAuthenticatedHeaderBytes(),
                                packet.HeaderAuthTag))
                        {
                            continue;
                        }

                        if (packet.IsTransmissionEnd && packet.Payload.Length == 0)
                        {
                            opusFrame = Array.Empty<byte>();
                        }
                        else
                        {
                            opusFrame = _secureCodec.DecryptToOpusFrame(
                                packet.Payload,
                                packet.Nonce,
                                packet.Tag,
                                net.Key!);
                            if (opusFrame == null) continue;
                        }
                    }
                    else if (netMatches)
                    {
                        opusFrame = packet.Payload;
                    }

                    // Undecodable on-frequency packets still contribute RF carrier energy.
                    bool canDecodeVoice = opusFrame != null;
                    var transmission = RadioTransmissionKey.FromPacket(packet);
                    bool isStartSignal = packet.IsTransmissionStart || packet.IsTransmissionStartHint;
                    var packetReceivedUtc = DateTime.UtcNow;
                    foreach (var expired in ExpireStaleRemoteTransmissions(
                        rxState,
                        transmission,
                        packetReceivedUtc,
                        StaleRemoteActivityFallbackTimeout))
                    {
                        EndRemoteReceiveHud(ch, rxState, expired.HudId);
                    }

                    bool wasActiveCarrier = rxState.Interference.IsActive(transmission);
                    bool wasPrimaryCarrier = rxState.Interference.ShouldAcceptAudioFrom(transmission);
                    bool isLocallyTransmitting = IsTransmitting(ch);

                    foreach (var expiredEnd in rxState.RecentlyEndedRemoteTransmissions
                        .Where(entry => entry.Value <= packetReceivedUtc)
                        .Select(entry => entry.Key)
                        .ToArray())
                    {
                        rxState.RecentlyEndedRemoteTransmissions.Remove(expiredEnd);
                    }
                    foreach (var expiredEnd in rxState.PendingEarlyEnds
                        .Where(entry => entry.Value.ExpiresUtc <= packetReceivedUtc)
                        .Select(entry => entry.Key)
                        .ToArray())
                    {
                        rxState.PendingEarlyEnds.Remove(expiredEnd);
                    }

                    var pendingHandoff = FindPendingReceiveHandoff(rxState, transmission);
                    if (!packet.IsTransmissionEnd && canDecodeVoice && pendingHandoff != null)
                    {
                        pendingHandoff.Add(new PendingRemoteFrame(
                            packet.Sequence,
                            opusFrame!,
                            isStartSignal,
                            IsEnd: false));
                        continue;
                    }

                    if (!packet.IsTransmissionEnd &&
                        rxState.RecentlyEndedRemoteTransmissions.ContainsKey(transmission))
                    {
                        // Accept late media without reopening carrier or lifecycle state.
                        if (canDecodeVoice && !isLocallyTransmitting &&
                            packet.TransmissionId != 0 &&
                            jitter.ActiveTransmissionId == packet.TransmissionId)
                        {
                            jitter.OnFrameReceived(
                                packet.TransmissionId,
                                packet.Sequence,
                                opusFrame!,
                                isStart: false,
                                isEnd: false,
                                JitterAcquisitionMode.None);
                        }
                        continue;
                    }

                    if (packet.IsTransmissionEnd && canDecodeVoice && pendingHandoff != null)
                    {
                        pendingHandoff.Add(new PendingRemoteFrame(
                            packet.Sequence,
                            opusFrame!,
                            IsStart: false,
                            IsEnd: true));
                        RemoveRemotePacketTimestamp(rxState, transmission);
                        rxState.TalkOver.ObserveRemoteTransmissionEnd(transmission);
                        rxState.Interference.ObserveTransmissionEnd(transmission);
                        RememberRecentlyEnded(rxState, transmission, packetReceivedUtc);
                        continue;
                    }

                    if (packet.IsTransmissionEnd)
                    {
                        // Only the first redundant End may mutate an active epoch.
                        if (!wasActiveCarrier)
                        {
                            if (transmission.TransmissionId != 0)
                            {
                                if (rxState.PendingEarlyEnds.Count >= MaxRecentlyEndedRemoteTransmissions)
                                {
                                    var oldest = rxState.PendingEarlyEnds
                                        .MinBy(entry => entry.Value.ExpiresUtc).Key;
                                    rxState.PendingEarlyEnds.Remove(oldest);
                                }
                                rxState.PendingEarlyEnds[transmission] = new PendingRemoteEnd(
                                    packet.Sequence,
                                    opusFrame ?? Array.Empty<byte>(),
                                    packetReceivedUtc + LateRemoteMediaGracePeriod);
                            }
                            continue;
                        }

                        rxState.LastRemotePacketUtc = packetReceivedUtc;
                        RemoveRemotePacketTimestamp(rxState, transmission);

                        bool deferHudEndUntilAudioDrains =
                            canDecodeVoice &&
                            ShouldDeferRemoteHudEndUntilAudioDrains(
                                wasPrimaryCarrier,
                                isLocallyTransmitting,
                                rxState.HasAudibleReceiveInFlight);

                        bool terminalAccepted = false;
                        if (wasPrimaryCarrier && canDecodeVoice && !isLocallyTransmitting)
                        {
                            terminalAccepted = jitter.OnFrameReceived(
                                packet.TransmissionId,
                                packet.Sequence,
                                opusFrame!,
                                isStart: false,
                                isEnd: true,
                                JitterAcquisitionMode.None);
                            if (terminalAccepted)
                                rxState.IsAudibleTransmissionTerminalPending = true;
                        }

                        rxState.TalkOver.ObserveRemoteTransmissionEnd(transmission);
                        rxState.Interference.ObserveTransmissionEnd(transmission);
                        RememberRecentlyEnded(rxState, transmission, packetReceivedUtc);
                        if (!rxState.Interference.HasInterference)
                            rxState.CollisionDestruction.Reset();

                        if (!deferHudEndUntilAudioDrains || !terminalAccepted)
                            EndRemoteReceiveHud(ch, rxState, transmission.HudId);
                        continue;
                    }

                    bool acquiredCarrier = !wasActiveCarrier;
                    InterferenceStartDecision carrierDecision;
                    if (isStartSignal)
                    {
                        rxState.TalkOver.ObserveRemoteTransmissionStart(transmission);
                        carrierDecision = rxState.Interference.ObserveTransmissionStart(transmission);
                    }
                    else if (acquiredCarrier || !rxState.Interference.HasPrimarySender)
                    {
                        // Reacquire after lost starts, late joins, or carrier handoff.
                        if (acquiredCarrier)
                            rxState.TalkOver.ObserveRemoteTransmissionStart(transmission);
                        carrierDecision = rxState.Interference.ObserveMidStreamTransmission(transmission);
                    }
                    else
                    {
                        bool isPrimary = rxState.Interference.ShouldAcceptAudioFrom(transmission);
                        carrierDecision = new InterferenceStartDecision
                        {
                            AcceptAudio = isPrimary,
                            IsPrimarySender = isPrimary,
                            IsInterferingSender = !isPrimary
                        };
                    }

                    rxState.LastRemotePacketUtc = packetReceivedUtc;
                    rxState.LastRemotePacketUtcByTransmission[transmission] = packetReceivedUtc;
                    rxState.LastRemotePacketUtcByClient[packet.ClientId] = packetReceivedUtc;
                    bool hasEarlyEnd = rxState.PendingEarlyEnds.TryGetValue(
                        transmission,
                        out var earlyEnd);

                    if (canDecodeVoice && (isStartSignal || acquiredCarrier))
                        StartRemoteReceiveHud(ch, rxState, transmission.HudId, packet.SenderName);

                    if (isLocallyTransmitting || !carrierDecision.AcceptAudio || !canDecodeVoice)
                    {
                        if (hasEarlyEnd)
                        {
                            rxState.PendingEarlyEnds.Remove(transmission);
                            rxState.TalkOver.ObserveRemoteTransmissionEnd(transmission);
                            rxState.Interference.ObserveTransmissionEnd(transmission);
                            RemoveRemotePacketTimestamp(rxState, transmission);
                            RememberRecentlyEnded(rxState, transmission, packetReceivedUtc);
                            if (canDecodeVoice)
                                EndRemoteReceiveHud(ch, rxState, transmission.HudId);
                        }
                        continue;
                    }

                    entry.vol.Volume = BoostVolume(ch.Volume);
                    entry.pan.Pan = EarToPan(ch.Ear);

                    bool followsTerminalStream = packet.TransmissionId != 0
                        ? jitter.ActiveTransmissionId != packet.TransmissionId
                        : rxState.ActiveAudibleTransmissionHudId != transmission.HudId;
                    if (rxState.IsAudibleTransmissionTerminalPending && followsTerminalStream)
                    {
                        pendingHandoff = FindPendingReceiveHandoff(rxState, transmission);
                        if (pendingHandoff == null &&
                            rxState.PendingHandoffs.Count < MaxPendingReceiveHandoffs)
                        {
                            pendingHandoff = new PendingReceiveHandoff
                            {
                                Transmission = transmission,
                                Callsign = packet.SenderName,
                                AudioSeed = RadioTransmissionNoiseSeed.Resolve(
                                    packet.TransmissionAudioSeed,
                                    opusFrame!),
                                ReceiverOffsetMHz = packet.Frequency - ch.Frequency
                            };
                            rxState.PendingHandoffs.Add(pendingHandoff);
                        }

                        if (pendingHandoff != null)
                        {
                            pendingHandoff.Add(new PendingRemoteFrame(
                                packet.Sequence,
                                opusFrame!,
                                isStartSignal,
                                IsEnd: false));
                            if (hasEarlyEnd)
                            {
                                pendingHandoff.Add(new PendingRemoteFrame(
                                    earlyEnd.Sequence,
                                    earlyEnd.OpusPayload,
                                    IsStart: false,
                                    IsEnd: true));
                                rxState.PendingEarlyEnds.Remove(transmission);
                                rxState.TalkOver.ObserveRemoteTransmissionEnd(transmission);
                                rxState.Interference.ObserveTransmissionEnd(transmission);
                                RemoveRemotePacketTimestamp(rxState, transmission);
                                RememberRecentlyEnded(rxState, transmission, packetReceivedUtc);
                            }
                        }
                        continue;
                    }

                    bool needsAcquisition = !isStartSignal &&
                        (acquiredCarrier ||
                         (packet.TransmissionId != 0 && jitter.ActiveTransmissionId != packet.TransmissionId));
                    var acquisitionMode = needsAcquisition
                        ? packet.TransmissionId == 0
                            ? JitterAcquisitionMode.Immediate
                            : JitterAcquisitionMode.Buffered
                        : JitterAcquisitionMode.None;

                    bool accepted = jitter.OnFrameReceived(
                        packet.TransmissionId,
                        packet.Sequence,
                        opusFrame!,
                        isStartSignal,
                        isEnd: false,
                        acquisitionMode);
                    if (!accepted) continue;

                    if (hasEarlyEnd)
                    {
                        bool terminalAccepted = jitter.OnFrameReceived(
                            packet.TransmissionId,
                            earlyEnd.Sequence,
                            earlyEnd.OpusPayload,
                            isStart: false,
                            isEnd: true,
                            JitterAcquisitionMode.None);
                        rxState.PendingEarlyEnds.Remove(transmission);
                        rxState.TalkOver.ObserveRemoteTransmissionEnd(transmission);
                        rxState.Interference.ObserveTransmissionEnd(transmission);
                        RemoveRemotePacketTimestamp(rxState, transmission);
                        RememberRecentlyEnded(rxState, transmission, packetReceivedUtc);
                        rxState.IsAudibleTransmissionTerminalPending = terminalAccepted;
                        if (!terminalAccepted)
                            EndRemoteReceiveHud(ch, rxState, transmission.HudId);
                    }

                    if (isStartSignal || needsAcquisition)
                    {
                        rxState.PendingTransmissionAudioSeed = RadioTransmissionNoiseSeed.Resolve(
                            packet.TransmissionAudioSeed,
                            opusFrame!);
                        rxState.PendingRemoteClientId = transmission.HudId;
                        rxState.PendingRemoteCallsign = packet.SenderName;
                        rxState.PendingReceiverOffsetMHz = packet.Frequency - ch.Frequency;
                        if (needsAcquisition)
                            ReacquireRemoteReceiveFromMidStream(rxState, packet, entry.buffer, transmission.HudId);
                    }

                    rxState.HasAudibleReceiveInFlight = true;
                }

            }
        }

        internal static bool ShouldDeferRemoteHudEndUntilAudioDrains(
            bool wasAcceptedSender,
            bool localTransmitting,
            bool hasAudibleReceiveInFlight) =>
            wasAcceptedSender &&
            hasAudibleReceiveInFlight &&
            !RadioReceiveMute.ShouldMuteReceivedAudio(localTransmitting);

        internal static bool RequiresAuthenticatedHeader(AudioPacket packet) =>
            packet.TransmissionId != 0 ||
            (packet.IsEncrypted && SecureAudioCodec.HasModernHeaderNonce(packet.Nonce));

        internal static bool ShouldFallbackClearStaleRemoteActivity(
            bool hasRemoteActivity,
            bool localTransmitting,
            bool isReceivingActive,
            DateTime? lastRemotePacketUtc,
            DateTime nowUtc,
            TimeSpan idleTimeout)
        {
            if (!hasRemoteActivity) return false;
            if (localTransmitting) return false;
            if (isReceivingActive) return false;
            if (lastRemotePacketUtc == null) return false;

            return nowUtc - lastRemotePacketUtc.Value >= idleTimeout;
        }

        internal static Guid[] ExpireStaleRemoteTransmitters(
            RxState rxState,
            Guid currentSenderId,
            DateTime nowUtc,
            TimeSpan idleTimeout)
        {
            var expiredSenderIds = rxState.LastRemotePacketUtcByClient
                .Where(entry => entry.Key != currentSenderId && nowUtc - entry.Value >= idleTimeout)
                .Select(entry => entry.Key)
                .ToArray();

            foreach (var senderId in expiredSenderIds)
            {
                rxState.LastRemotePacketUtcByClient.Remove(senderId);
                rxState.TalkOver.ObserveRemoteTransmissionEnd(senderId);
                rxState.Interference.ObserveTransmissionEnd(senderId);
            }

            if (!rxState.Interference.HasInterference)
                rxState.CollisionDestruction.Reset();

            return expiredSenderIds;
        }

        internal static RadioTransmissionKey[] ExpireStaleRemoteTransmissions(
            RxState rxState,
            RadioTransmissionKey currentTransmission,
            DateTime nowUtc,
            TimeSpan idleTimeout)
        {
            var expired = rxState.LastRemotePacketUtcByTransmission
                .Where(entry => entry.Key != currentTransmission && nowUtc - entry.Value >= idleTimeout)
                .Select(entry => entry.Key)
                .ToArray();

            foreach (var transmission in expired)
            {
                rxState.LastRemotePacketUtcByTransmission.Remove(transmission);
                rxState.TalkOver.ObserveRemoteTransmissionEnd(transmission);
                rxState.Interference.ObserveTransmissionEnd(transmission);
                if (!rxState.LastRemotePacketUtcByTransmission.Keys.Any(
                        key => key.ClientId == transmission.ClientId))
                {
                    rxState.LastRemotePacketUtcByClient.Remove(transmission.ClientId);
                }
            }

            if (!rxState.Interference.HasInterference)
                rxState.CollisionDestruction.Reset();

            return expired;
        }

        private static void RemoveRemotePacketTimestamp(
            RxState rxState,
            RadioTransmissionKey transmission)
        {
            rxState.LastRemotePacketUtcByTransmission.Remove(transmission);
            if (!rxState.LastRemotePacketUtcByTransmission.Keys.Any(
                    key => key.ClientId == transmission.ClientId))
            {
                rxState.LastRemotePacketUtcByClient.Remove(transmission.ClientId);
            }
        }

        private static void RememberRecentlyEnded(
            RxState rxState,
            RadioTransmissionKey transmission,
            DateTime endedUtc)
        {
            if (transmission.TransmissionId == 0) return;
            if (rxState.RecentlyEndedRemoteTransmissions.Count >=
                MaxRecentlyEndedRemoteTransmissions)
            {
                var oldest = rxState.RecentlyEndedRemoteTransmissions
                    .MinBy(entry => entry.Value).Key;
                rxState.RecentlyEndedRemoteTransmissions.Remove(oldest);
            }
            rxState.RecentlyEndedRemoteTransmissions[transmission] =
                endedUtc + LateRemoteMediaGracePeriod;
        }

        internal static void ReacquireRemoteReceiveFromMidStream(
            RxState rxState,
            AudioPacket packet,
            BufferedWaveProvider? receiveBuffer = null,
            string? remoteHudId = null)
        {
            rxState.PendingRemoteClientId = remoteHudId ?? packet.ClientId.ToString();
            rxState.PendingRemoteCallsign = packet.SenderName;
            rxState.CollisionDestruction.Reset();

            // Clear queued collision audio when reacquiring a stream mid-transmission.
            if (receiveBuffer != null)
            {
                lock (receiveBuffer)
                    receiveBuffer.ClearBuffer();
            }
        }

        internal static void ClearLocalTalkOverWarning(RxState rxState, BufferedWaveProvider sidetoneBuffer)
        {
            rxState.TalkOverWarningCue.Reset();
            lock (sidetoneBuffer)
                sidetoneBuffer.ClearBuffer();
        }

        internal static void EndAllRemoteReceiveHuds(
            RadioChannel channel,
            RxState rxState,
            Action<RadioChannel, string, string> end)
        {
            if (!rxState.IsReceiveHudActive) return;

            foreach (var kvp in rxState.ActiveRemoteHudByClient.ToArray())
            {
                rxState.ActiveRemoteHudByClient.Remove(kvp.Key);
                end(channel, kvp.Value, kvp.Key);
            }

            RefreshPrimaryRemoteHudOwner(rxState);
            rxState.PendingRemoteClientId = "";
            rxState.PendingRemoteCallsign = "";
        }

        internal static bool PacketMatchesChannelForReceiveControl(RadioChannel channel, AudioPacket packet)
        {
            const float tolerance = 0.005f;
            if (Math.Abs(channel.Frequency - packet.Frequency) > tolerance) return false;

            var net = channel.SelectedNet;
            if (net.IsUnencrypted)
                return !packet.IsEncrypted && NetIdHashesEqual(packet.NetIdHash, new byte[8]);

            return NetIdHashesEqual(net.NetIdHash, packet.NetIdHash);
        }

        internal static void ProcessRemoteReceiveHudControl(
            RadioChannel channel,
            RxState rxState,
            AudioPacket packet,
            Action<RadioChannel, string> start,
            Action<RadioChannel, string> end)
        {
            string remoteClientId = packet.ClientId.ToString();

            if (packet.IsTransmissionStart)
            {
                if (!rxState.ActiveRemoteHudByClient.ContainsKey(remoteClientId))
                {
                    rxState.ActiveRemoteHudByClient[remoteClientId] = packet.SenderName;
                    RefreshPrimaryRemoteHudOwner(rxState);
                    start(channel, packet.SenderName);
                }
            }

            if (packet.IsTransmissionEnd)
            {
                if (!rxState.ActiveRemoteHudByClient.TryGetValue(remoteClientId, out var remoteCallsign)) return;

                rxState.ActiveRemoteHudByClient.Remove(remoteClientId);
                RefreshPrimaryRemoteHudOwner(rxState);
                if (!rxState.IsReceiveHudActive)
                {
                    rxState.PendingRemoteClientId = "";
                    rxState.PendingRemoteCallsign = "";
                }
                end(channel, remoteCallsign);
            }
        }

        internal static void ProcessMutedRemoteReceiveControl(
            RadioChannel channel,
            RxState rxState,
            AudioPacket packet,
            Action<RadioChannel, string, string> start,
            Action<RadioChannel, string, string> end)
        {
            string remoteClientId = packet.ClientId.ToString();

            if (packet.IsTransmissionStart)
            {
                rxState.TalkOver.ObserveRemoteTransmissionStart(packet.ClientId);

                if (!rxState.ActiveRemoteHudByClient.ContainsKey(remoteClientId))
                {
                    rxState.ActiveRemoteHudByClient[remoteClientId] = packet.SenderName;
                    RefreshPrimaryRemoteHudOwner(rxState);
                    start(channel, packet.SenderName, remoteClientId);
                }
            }

            if (packet.IsTransmissionEnd)
            {
                rxState.TalkOver.ObserveRemoteTransmissionEnd(packet.ClientId);
                rxState.Interference.ObserveTransmissionEnd(packet.ClientId);
                if (!rxState.Interference.HasInterference)
                    rxState.CollisionDestruction.Reset();

                if (!rxState.ActiveRemoteHudByClient.TryGetValue(remoteClientId, out var remoteCallsign)) return;

                rxState.ActiveRemoteHudByClient.Remove(remoteClientId);
                RefreshPrimaryRemoteHudOwner(rxState);
                if (!rxState.IsReceiveHudActive)
                {
                    rxState.PendingRemoteClientId = "";
                    rxState.PendingRemoteCallsign = "";
                }
                end(channel, remoteCallsign, remoteClientId);
            }
        }

        private static void RefreshPrimaryRemoteHudOwner(RxState rxState)
        {
            if (rxState.ActiveRemoteHudByClient.Count == 0)
            {
                rxState.ActiveRemoteClientId = "";
                rxState.ActiveRemoteCallsign = "";
                return;
            }

            var first = rxState.ActiveRemoteHudByClient.First();
            rxState.ActiveRemoteClientId = first.Key;
            rxState.ActiveRemoteCallsign = first.Value;
        }

        private static bool NetIdHashesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private void StartRemoteReceiveHud(RadioChannel channel, RxState rxState, string remoteClientId, string remoteCallsign)
        {
            if (rxState.ActiveRemoteHudByClient.ContainsKey(remoteClientId)) return;

            rxState.ActiveRemoteHudByClient[remoteClientId] = remoteCallsign;
            RefreshPrimaryRemoteHudOwner(rxState);
            RaiseTransmissionStarted(new TransmissionEventArgs
            {
                Channel = channel,
                IsLocalTransmit = false,
                RemoteCallsign = remoteCallsign,
                RemoteClientId = remoteClientId,
                LifecycleSequence = NextTransmissionLifecycleSequence()
            });
        }

        private void EndRemoteReceiveHud(RadioChannel channel, RxState rxState, string? remoteClientId = null)
        {
            if (!rxState.IsReceiveHudActive) return;

            string idToEnd = string.IsNullOrEmpty(remoteClientId)
                ? rxState.ActiveRemoteClientId
                : remoteClientId;
            if (string.IsNullOrEmpty(idToEnd)) return;
            if (!rxState.ActiveRemoteHudByClient.TryGetValue(idToEnd, out var remoteCallsign)) return;

            rxState.ActiveRemoteHudByClient.Remove(idToEnd);
            RefreshPrimaryRemoteHudOwner(rxState);
            if (!rxState.IsReceiveHudActive)
            {
                rxState.PendingRemoteClientId = "";
                rxState.PendingRemoteCallsign = "";
            }

            RaiseRemoteTransmissionEnded(channel, remoteCallsign, idToEnd);
        }

        private void RaiseRemoteTransmissionEnded(RadioChannel channel, string remoteCallsign, string remoteClientId)
        {
            RaiseTransmissionEnded(new TransmissionEventArgs
            {
                Channel = channel,
                IsLocalTransmit = false,
                RemoteCallsign = remoteCallsign,
                RemoteClientId = remoteClientId,
                LifecycleSequence = NextTransmissionLifecycleSequence()
            });
        }

        private void TickJitterBuffers()
        {
            lock (_stateLock)
            {
                foreach (var ch in Channels)
                {
                    if (!_jitterBuffers.TryGetValue(ch, out var jitter)) continue;
                    if (!_channelBuffers.TryGetValue(ch, out var entry)) continue;
                    if (!_rxState.TryGetValue(ch, out var rxState)) continue;

                    if (RadioReceiveMute.IsReceiveDisabled(ch.Volume))
                    {
                        DisableChannelReceive(ch, rxState);
                        continue;
                    }

                    QueueTalkOverWarningFrame(ch, rxState);

                    var result = jitter.Tick();

                    if (result.IsFirstFrame)
                    {
                        rxState.IsReceivingActive = true;
                        rxState.ActiveAudibleTransmissionHudId = rxState.PendingRemoteClientId;
                        GetRxProfile(ch, rxState).ResetReceive();
                        rxState.Noise.Reset(rxState.PendingTransmissionAudioSeed);
                        rxState.Detuning.Reset(
                            rxState.PendingReceiverOffsetMHz,
                            RadioBandExtensions.FromFrequencyMHz(ch.Frequency),
                            ch.IsIntercom,
                            rxState.PendingTransmissionAudioSeed);
                        StartRemoteReceiveHud(ch, rxState, rxState.PendingRemoteClientId, rxState.PendingRemoteCallsign);
                        var startClick = RenderFilteredReceiveCue(
                            ch,
                            rxState,
                            SoundLibrary.RxStart,
                            FilteredReceiveStartCueGain);
                        entry.buffer.AddSamples(startClick, 0, startClick.Length);
                    }

                    if (rxState.IsReceivingActive)
                    {
                        var profile = GetRxProfile(ch, rxState);
                        var frame = RadioReceiveFrameProcessor.Process(
                            result.Pcm,
                            ch,
                            profile,
                            rxState.Noise);
                        rxState.Detuning.Process(frame);
                        MixCoChannelInterference(ch, rxState, frame);

                        var bytes = FloatsToPcm(frame);
                        entry.buffer.AddSamples(bytes, 0, bytes.Length);
                    }

                    // Queue the end cue after the final decoded frame.
                    if (result.IsLastFrame)
                    {
                        bool hadActiveReceivePlayout = rxState.IsReceivingActive;
                        rxState.IsReceivingActive = false;
                        rxState.IsAudibleTransmissionTerminalPending = false;
                        rxState.TalkOverWarningCue.Reset();
                        rxState.HasAudibleReceiveInFlight = false;
                        // Avoid an orphan end cue when the first Opus frame is malformed.
                        if (hadActiveReceivePlayout)
                        {
                            var endClick = RenderFilteredReceiveCue(
                                ch,
                                rxState,
                                SoundLibrary.RxEnd,
                                FilteredReceiveEndCueGain);
                            entry.buffer.AddSamples(endClick, 0, endClick.Length);
                        }
                        if (!string.IsNullOrEmpty(rxState.ActiveAudibleTransmissionHudId))
                            EndRemoteReceiveHud(ch, rxState, rxState.ActiveAudibleTransmissionHudId);
                        rxState.ActiveAudibleTransmissionHudId = "";
                        ActivatePendingReceiveHandoff(rxState, jitter);
                    }

                    bool hasRemoteActivity = rxState.IsReceiveHudActive ||
                        rxState.TalkOver.HasRemoteTransmitters ||
                        rxState.Interference.HasPrimarySender;
                    if (ShouldFallbackClearStaleRemoteActivity(
                        hasRemoteActivity,
                        IsTransmitting(ch),
                        rxState.IsReceivingActive,
                        rxState.LastRemotePacketUtc,
                        DateTime.UtcNow,
                        StaleRemoteActivityFallbackTimeout))
                    {
                        jitter.Reset();
                        rxState.TalkOver.ClearRemoteTransmitters();
                        rxState.TalkOverWarningCue.Reset();
                        rxState.Interference.Reset();
                        rxState.CollisionDestruction.Reset();
                        rxState.IsReceivingActive = false;
                        rxState.HasAudibleReceiveInFlight = false;
                        rxState.ActiveAudibleTransmissionHudId = "";
                        rxState.LastRemotePacketUtc = null;
                        rxState.LastRemotePacketUtcByClient.Clear();
                        rxState.LastRemotePacketUtcByTransmission.Clear();
                        rxState.RecentlyEndedRemoteTransmissions.Clear();
                        rxState.PendingEarlyEnds.Clear();
                        rxState.PendingHandoffs.Clear();
                        rxState.IsAudibleTransmissionTerminalPending = false;
                        EndAllRemoteReceiveHuds(ch, rxState, (_, remoteCallsign, remoteClientId) =>
                            RaiseRemoteTransmissionEnded(ch, remoteCallsign, remoteClientId));
                    }
                }

            }
        }

        internal static void ActivatePendingReceiveHandoff(RxState rxState, JitterBuffer jitter)
        {
            PendingReceiveHandoff? pending = null;
            while (rxState.PendingHandoffs.Count > 0)
            {
                pending = rxState.PendingHandoffs[0];
                rxState.PendingHandoffs.RemoveAt(0);
                if (pending.Frames.Count > 0) break;
                pending = null;
            }
            if (pending == null) return;

            rxState.PendingTransmissionAudioSeed = pending.AudioSeed;
            rxState.PendingRemoteClientId = pending.Transmission.HudId;
            rxState.PendingRemoteCallsign = pending.Callsign;
            rxState.PendingReceiverOffsetMHz = pending.ReceiverOffsetMHz;

            bool acceptedAny = false;
            bool acceptedTerminal = false;
            for (int i = 0; i < pending.Frames.Count; i++)
            {
                var frame = pending.Frames[i];
                bool establish = i == 0;
                bool accepted = jitter.OnFrameReceived(
                    pending.Transmission.TransmissionId,
                    frame.Sequence,
                    frame.OpusPayload,
                    isStart: establish || frame.IsStart,
                    isEnd: frame.IsEnd,
                    establish ? JitterAcquisitionMode.Buffered : JitterAcquisitionMode.None);
                acceptedAny |= accepted;
                acceptedTerminal |= accepted && frame.IsEnd;
            }

            rxState.HasAudibleReceiveInFlight = acceptedAny;
            rxState.IsAudibleTransmissionTerminalPending = acceptedTerminal;
            if (acceptedAny)
            {
                // Give deferred playout a fresh stale timeout when it becomes active.
                rxState.LastRemotePacketUtc = DateTime.UtcNow;
            }
        }

        private static PendingReceiveHandoff? FindPendingReceiveHandoff(
            RxState rxState,
            RadioTransmissionKey transmission) =>
            rxState.PendingHandoffs.FirstOrDefault(candidate =>
                candidate.Transmission == transmission);

        /// <summary>
        /// Applies destructive co-channel interference to captured audio.
        /// </summary>
        private void MixCoChannelInterference(RadioChannel channel, RxState rxState, float[] frame)
        {
            if (channel.IsIntercom) return;
            rxState.CollisionDestruction.Process(
                frame,
                rxState.Interference.HasInterference,
                RadioBandExtensions.FromFrequencyMHz(channel.Frequency));
        }

        private byte[] RenderFilteredReceiveCue(
            RadioChannel channel,
            RxState rxState,
            float[] cue,
            float postFilterGain)
        {
            var profile = GetRxProfile(channel, rxState);
            var filtered = RadioReceiveFrameProcessor.ProcessSamples(
                ScaleVolume(cue, OutputClickVolume),
                channel,
                profile,
                rxState.Noise);
            rxState.Detuning.Process(filtered);
            MixCoChannelInterference(channel, rxState, filtered);
            return FloatsToPcm(ScaleVolume(filtered, postFilterGain));
        }

        /// <summary>
        /// Plays the centered connection cue.
        /// </summary>
        public void PlayConnectedBeep()
        {
            lock (_stateLock)
            {
                var pcm = FloatsToPcm(SoundLibrary.BeepConnected);
                ReplaceBufferedSamples(_systemSoundBuffer, pcm);

            }
        }

        /// <summary>
        /// Plays the centered disconnection cue.
        /// </summary>
        public void PlayDisconnectedBeep()
        {
            lock (_stateLock)
            {
                var pcm = FloatsToPcm(SoundLibrary.BeepDisconnected);
                ReplaceBufferedSamples(_systemSoundBuffer, pcm);

            }
        }

        internal static void ReplaceBufferedSamples(BufferedWaveProvider buffer, byte[] pcm)
        {
            lock (buffer)
            {
                buffer.ClearBuffer();
                buffer.AddSamples(pcm, 0, pcm.Length);
            }
        }

        private void QueueTalkOverWarningFrame(RadioChannel channel, RxState rxState)
        {
            if (RadioReceiveMute.IsReceiveDisabled(channel.Volume)) return;
            if (!rxState.TalkOver.HasActiveOverlap) return;
            if (!_sidetoneBuffers.TryGetValue(channel, out var sidetone)) return;

            const int maxQueuedFrames = 4;
            if (sidetone.buffer.BufferedBytes > FrameSize * 2 * maxQueuedFrames) return;

            var frame = rxState.TalkOverWarningCue.ReadFrame(FrameSize, TalkOverWarningVolume);
            var pcm = FloatsToPcm(frame);
            sidetone.buffer.AddSamples(pcm, 0, pcm.Length);
        }

        private void PlayLocalSidetone(RadioChannel channel, float[] samples)
        {
            if (!_sidetoneBuffers.TryGetValue(channel, out var sidetone)) return;
            var pcm = FloatsToPcm(ScaleVolume(samples, BoostVolume(InputClickVolume)));
            sidetone.buffer.AddSamples(pcm, 0, pcm.Length);
        }

        private static float[] ScaleVolume(float[] samples, float volume)
        {
            if (volume == 1f) return samples;
            var scaled = new float[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                scaled[i] = Math.Clamp(samples[i] * volume, -1f, 1f);
            return scaled;
        }

        private static byte[] FloatsToPcm(float[] samples)
        {
            var pcm = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
                BitConverter.GetBytes(s).CopyTo(pcm, i * 2);
            }
            return pcm;
        }

        public void Dispose()
        {
            System.Threading.Thread? micProcessingThread;
            lock (_stateLock)
            {
                if (_disposed) return;
                _disposed = true;
                _jitterTicker.Dispose();
                if (_waveIn != null)
                    _waveIn.DataAvailable -= OnMicDataAvailable;
                _waveIn?.StopRecording();
                _waveIn?.Dispose();
                _waveIn = null;
                lock (_micCaptureProducerLock)
                    _micProcessingStopping = true;
                while (_micCaptureQueue.TryDequeue(out _))
                    System.Threading.Interlocked.Decrement(ref _micCaptureQueueCount);
                System.Threading.Interlocked.Exchange(ref _micCaptureQueueCount, 0);
                _micCaptureSignal.Set();
                micProcessingThread = _micProcessingThread;
                _micProcessingThread = null;
                _isMicTestActive = false;
                lock (_micTestBuffer)
                    _micTestBuffer.ClearBuffer();
                _waveOut?.Stop();
                _waveOut?.Dispose();
                _waveOut = null;
                _passthroughWasapiOut?.Stop();
                _passthroughWasapiOut?.Dispose();
                _passthroughWasapiOut = null;
                _passthroughDevice?.Dispose();
                _passthroughDevice = null;
                _passthroughDeviceId = null;
                _passthroughProcessor.Reset();
                _passthroughOutputConverter.Reset();
                _secureCodec.ClearTransmitStreams();
                lock (_passthroughBuffer)
                    _passthroughBuffer.ClearBuffer();

            }

            if (micProcessingThread != null && micProcessingThread != System.Threading.Thread.CurrentThread)
                micProcessingThread.Join(TimeSpan.FromSeconds(1));
            _micCaptureSignal.Dispose();
            _applicationAmbience.Dispose();
        }

    }
}
