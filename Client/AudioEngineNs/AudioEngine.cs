using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RadioRelay.Client.Networking;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Audio;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Client.AudioEngineNs
{
    public class CapturedAudioEventArgs : EventArgs
    {
        public AudioPacket Packet = new();
    }

    /// Raised whenever a transmission actually starts/ends playing
    /// out (received audio) or actually starts/ends being sent (local
    /// transmit), so the UI can drive the on-screen transmission HUD off
    /// real audio activity rather than raw packet timing.
    public class TransmissionEventArgs : EventArgs
    {
        public required RadioChannel Channel { get; init; }
        public required bool IsLocalTransmit { get; init; } // true = we're sending, false = we're receiving
        public string RemoteCallsign { get; init; } = "";
        public string RemoteClientId { get; init; } = "";
        public long LifecycleSequence { get; init; }
    }

    /// Per-channel transmit state: each radio has its own PTT, its
    /// own passcode-derived net, and its own sequence/PCM accumulation, so
    /// multiple radios can be keyed independently (even simultaneously).
    internal class TxState
    {
        public bool IsActive;
        public ushort Sequence;
        public readonly List<short> Accumulator = new();
    }

    /// Per-channel receive state used to know when a looping
    /// band-noise bed should be playing underneath decoded voice, and to
    /// track simple FM-style co-channel interference: the first audible
    /// sender captures the receiver while later overlapping audible senders
    /// add interference noise rather than playing a standalone local squelch
    /// cue or taking over the jitter stream mid-transmission.
    internal class RxState
    {
        public bool IsReceivingActive;
        public int NoiseLoopPosition;
        public RadioInterferenceTracker Interference { get; } = new();
        public RadioTalkOverMonitor TalkOver { get; } = new();
        public LoopingAudioCue TalkOverWarningCue { get; } = new(SoundLibrary.Collision);
        public RadioCollisionDestructionModel CollisionDestruction { get; } = new(AudioEngine.SampleRate);
        public RadioBand CachedBand;
        public RadioEffectProfile? CachedProfile;

        // UI/HUD receive state is tracked separately from the low-level
        // jitter/interference state so every remote TransmissionStarted we
        // raise has exactly one matching TransmissionEnded, even when an
        // overlapping co-channel sender is rejected by the capture model.
        public bool IsReceiveHudActive => ActiveRemoteHudByClient.Count > 0;
        public string ActiveRemoteClientId = "";
        public string ActiveRemoteCallsign = "";
        public readonly Dictionary<string, string> ActiveRemoteHudByClient = new();
        public string PendingRemoteClientId = "";
        public string PendingRemoteCallsign = "";
        public bool HasAudibleReceiveInFlight;
        public DateTime? LastRemotePacketUtc;
    }

    /// 
    /// Owns the microphone input and speaker output. Pipeline on transmit:
    /// mic PCM -> tunable per-band DSP effect chain (highpass/peak/lowpass
    /// filters, saturation, sidechain compression, gain -- structured after
    /// the DCS-SRS effect-graph schema) -> optional cosmetic CVSD
    /// "encrypted radio" character -> Opus encode -> optional AES-GCM
    /// encrypt -> AudioPacket, done independently per currently-keyed radio.
    /// Pipeline on receive: decrypt -> jitter buffer (which itself
    /// Opus-decodes on a steady 20ms clock) -> per-band rxEffect -> looping
    /// recorded band-noise bed mixed underneath while actively receiving ->
    /// per-channel volume -> stereo pan (left/right/both, per that radio's
    /// Ear setting) -> mixed stereo output. Key clicks and
    /// connect/disconnect beeps are recorded assets (see SoundLibrary).
    /// Co-channel radio interference is modeled as capture-effect
    /// arbitration plus destructive sample damage for listeners, while local
    /// transmitters hear a continuous talk-over warning cue for the duration
    /// of overlap.
    /// 
    public class AudioEngine : IDisposable
    {
        public const int SampleRate = 16000;
        private static readonly WaveFormat Format = new(SampleRate, 16, 1);
        private const int FrameSize = OpusCodec.FrameSize; // 320 samples = 20ms
        internal const int TransmissionEndRedundantPacketCount = 3;
        internal static readonly TimeSpan StaleRemoteHudFallbackTimeout = TimeSpan.FromMilliseconds(500);

        private WaveInEvent? _waveIn;
        private WaveOutEvent? _waveOut;
        private int _inputDeviceIndex;
        private int _outputDeviceIndex;
        private readonly MixingSampleProvider _mixer; // stereo
        private readonly Dictionary<RadioChannel, (BufferedWaveProvider buffer, VolumeSampleProvider vol, PanningSampleProvider pan)> _channelBuffers = new();
        private readonly Dictionary<RadioChannel, JitterBuffer> _jitterBuffers = new();
        private readonly Dictionary<RadioChannel, RxState> _rxState = new();
        private readonly Dictionary<RadioChannel, (BufferedWaveProvider buffer, PanningSampleProvider pan)> _sidetoneBuffers = new();
        private readonly Dictionary<RadioChannel, TxState> _txState = new();
        private readonly Dictionary<RadioChannel, (RadioBand band, RadioEffectProfile profile)> _txProfiles = new();
        private readonly BufferedWaveProvider _systemSoundBuffer;
        private readonly SecureAudioCodec _secureCodec;
        private readonly System.Threading.Timer _jitterTicker;
        private readonly object _stateLock = new();
        private long _transmissionLifecycleSequence;

        public List<RadioChannel> Channels { get; }

        /// The user's self-assigned callsign, stamped onto every
        /// packet this engine sends. Settable live -- no login required.
        public string Callsign { get; set; } = "";

        /// Linear gain applied to the microphone signal before the
        /// radio DSP effect and Opus encoding. 1.0 = unity. Settable live.
        public float InputGain { get; set; } = 1.0f;

        /// Volume, 0..1, of the click you hear yourself when you key
        /// or release your own PTT (independent of any radio's listen
        /// Volume -- you should still hear your own key click even if you've
        /// turned a radio's listen volume down).
        public float InputClickVolume { get; set; } = 1.0f;

        /// Volume, 0..1, of the continuous talk-over warning squelch
        /// played only to a local transmitter while another audible station
        /// overlaps this radio. Deliberately separate from TX click volume so
        /// disabling key-clicks cannot make the radio receive while transmitting.
        public float TalkOverWarningVolume { get; set; } = 1.0f;

        /// Volume, 0..1, of the click played when a received
        /// transmission starts/ends. Multiplies on top of that radio's own
        /// listen Volume, same as the voice audio itself.
        public float OutputClickVolume { get; set; } = 1.0f;

        private const float AppVolumeBoost = 3.0f;

        private static float BoostVolume(float volume)
        {
            return Math.Max(0f, volume * AppVolumeBoost);
        }

        /// Raised whenever a fully-encoded (and possibly encrypted) packet is ready to send.
        public event EventHandler<CapturedAudioEventArgs>? AudioCaptured;

        /// Raised when a transmission (local or remote) actually
        /// starts or stops producing audible audio -- drives the on-screen
        /// transmission HUD.
        public event EventHandler<TransmissionEventArgs>? TransmissionStarted;
        public event EventHandler<TransmissionEventArgs>? TransmissionEnded;

        public AudioEngine(List<RadioChannel> channels, int micDeviceIndex = -1, int speakerDeviceIndex = -1, bool startAudioDevices = true)
        {
            Channels = channels;
            _secureCodec = new SecureAudioCodec(SampleRate);
            _inputDeviceIndex = micDeviceIndex;
            _outputDeviceIndex = speakerDeviceIndex;

            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2))
            {
                ReadFully = true
            };

            // Long enough to comfortably hold the longest connect/disconnect
            // beep (~6s) added in a single one-shot AddSamples call.
            _systemSoundBuffer = new BufferedWaveProvider(Format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(10)
            };
            _mixer.AddMixerInput(new PanningSampleProvider(_systemSoundBuffer.ToSampleProvider()) { Pan = 0f });

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
                _waveOut = new WaveOutEvent { DeviceNumber = _outputDeviceIndex };
                _waveOut.Init(_mixer);
                _waveOut.Play();

                _waveIn = new WaveInEvent
                {
                    DeviceNumber = _inputDeviceIndex,
                    WaveFormat = Format,
                    BufferMilliseconds = 40
                };
                _waveIn.DataAvailable += OnMicDataAvailable;
                _waveIn.StartRecording();
            }

            // Drives playout for every channel's jitter buffer at a steady
            // cadence matching the Opus frame size, independent of however
            // bursty/irregular network arrival actually is.
            _jitterTicker = new System.Threading.Timer(_ => RunBackgroundCallback(TickJitterBuffers), null, 20, 20);
        }

        /// Switches the microphone device live, without needing to
        /// reconnect or lose any radio/PTT/encryption state. deviceIndex
        /// values come from AudioDeviceEnumerator.GetInputDevices() (-1 =
        /// system default).
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
                    BufferMilliseconds = 40
                };
                _waveIn.DataAvailable += OnMicDataAvailable;
                _waveIn.StartRecording();

            }
        }

        /// Switches the speaker/output device live. deviceIndex
        /// values come from AudioDeviceEnumerator.GetOutputDevices() (-1 =
        /// system default).
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

                _waveOut = new WaveOutEvent { DeviceNumber = _outputDeviceIndex };
                _waveOut.Init(_mixer);
                _waveOut.Play();

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
            // Deliberately NOT routed through that channel's Volume (a
            // "listen" volume of 0 shouldn't also silence your own
            // confirmation click that you're transmitting) -- but it IS
            // routed through that channel's Ear/pan setting, so your own
            // click plays on the same side as everything else for that radio.
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

        /// Call immediately when a radio's Ear selection changes, so
        /// both its received audio and its own sidetone click re-pan right
        /// away rather than waiting for the next received packet.
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

        /// Gets (rebuilding if the channel's band has changed since
        /// last time) the tunable txEffect/rxEffect/noise profile for
        /// whatever band this channel's frequency currently falls in.
        private RadioEffectProfile GetTxProfile(RadioChannel channel)
        {
            var band = RadioBandExtensions.FromFrequencyMHz(channel.Frequency);
            if (_txProfiles.TryGetValue(channel, out var cached) && cached.band == band)
                return cached.profile;

            var profile = RadioEffectProfile.ForBand(band, channel.IsIntercom, SampleRate);
            _txProfiles[channel] = (band, profile);
            return profile;
        }

        private RadioEffectProfile GetRxProfile(RadioChannel channel, RxState state)
        {
            var band = RadioBandExtensions.FromFrequencyMHz(channel.Frequency);
            if (state.CachedProfile != null && state.CachedBand == band) return state.CachedProfile;

            var profile = RadioEffectProfile.ForBand(band, channel.IsIntercom, SampleRate);
            state.CachedBand = band;
            state.CachedProfile = profile;
            return profile;
        }

        /// Call on a specific radio's PTT engage/release (after any
        /// configured release delay has elapsed). Independent per channel --
        /// other radios' transmit state is unaffected.
        public void SetTransmitting(RadioChannel channel, bool active)
        {
            lock (_stateLock)
            {
                if (!_txState.TryGetValue(channel, out var state)) return;
                if (active == state.IsActive) return;
                state.IsActive = active;

                if (_rxState.TryGetValue(channel, out var rxState))
                {
                    rxState.TalkOver.SetLocalTransmitting(active);
                    if (active)
                    {
                        // Local TX should mute/clear the audible receive path, but
                        // it must NOT close the RX HUD. RX is remote activity on
                        // the frequency, and the operator needs to keep seeing who
                        // else is transmitting while they are talking over them.
                        if (_jitterBuffers.TryGetValue(channel, out var jitter))
                            jitter.Reset();
                        if (_channelBuffers.TryGetValue(channel, out var receiveEntry))
                            receiveEntry.buffer.ClearBuffer();
                        rxState.IsReceivingActive = false;
                        rxState.PendingRemoteClientId = "";
                        rxState.PendingRemoteCallsign = "";
                        rxState.HasAudibleReceiveInFlight = false;
                        rxState.Interference.Reset();
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

                PlayLocalSidetone(channel, active ? SoundLibrary.TxStart : SoundLibrary.TxEnd);

                if (!active)
                {
                    state.Accumulator.Clear();
                    foreach (var packet in CreateTransmissionEndPackets(channel, state.Sequence, Callsign))
                        RaiseAudioCaptured(new CapturedAudioEventArgs { Packet = packet });
                    RaiseTransmissionEnded(new TransmissionEventArgs { Channel = channel, IsLocalTransmit = true, LifecycleSequence = NextTransmissionLifecycleSequence() });
                }
                else
                {
                    state.Sequence = 0;
                    state.Accumulator.Clear();
                    RaiseTransmissionStarted(new TransmissionEventArgs { Channel = channel, IsLocalTransmit = true, RemoteCallsign = Callsign, LifecycleSequence = NextTransmissionLifecycleSequence() });
                }

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
                catch { /* Subscriber failures must not escape audio callbacks or timer ticks. */ }
            }
        }

        internal static IReadOnlyList<AudioPacket> CreateTransmissionEndPackets(
            RadioChannel channel,
            ushort sequence,
            string callsign)
        {
            var packets = new List<AudioPacket>(TransmissionEndRedundantPacketCount);
            var net = channel.SelectedNet;
            for (int i = 0; i < TransmissionEndRedundantPacketCount; i++)
            {
                packets.Add(new AudioPacket
                {
                    Frequency = channel.Frequency,
                    Sequence = sequence,
                    IsTransmissionStart = false,
                    IsTransmissionEnd = true,
                    IsEncrypted = !net.IsUnencrypted,
                    NetIdHash = net.NetIdHash,
                    Nonce = !net.IsUnencrypted ? new byte[12] : null,
                    Tag = !net.IsUnencrypted ? new byte[16] : null,
                    SenderName = callsign,
                    RadioName = channel.Name,
                    Payload = Array.Empty<byte>()
                });
            }

            return packets;
        }

        private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
        {
            RunBackgroundCallback(() => ProcessMicDataAvailable(e));
        }

        private void ProcessMicDataAvailable(WaveInEventArgs e)
        {
            lock (_stateLock)
            {
                int sampleCount = e.BytesRecorded / 2;

                foreach (var kvp in _txState)
                {
                    var channel = kvp.Key;
                    var state = kvp.Value;
                    if (!state.IsActive) continue;

                    for (int i = 0; i < sampleCount; i++)
                        state.Accumulator.Add(BitConverter.ToInt16(e.Buffer, i * 2));

                    // WaveInEvent buffers rarely align exactly to 320 samples, so we
                    // accumulate and slice out exact 20ms Opus frames as they become available.
                    while (state.Accumulator.Count >= FrameSize)
                    {
                        var frame = state.Accumulator.GetRange(0, FrameSize).ToArray();
                        state.Accumulator.RemoveRange(0, FrameSize);
                        SendFrame(channel, state, frame);
                    }
                }

            }
        }

        private void SendFrame(RadioChannel txChannel, TxState state, short[] frame)
        {
            var floatSamples = new float[FrameSize];
            for (int i = 0; i < FrameSize; i++)
                floatSamples[i] = Math.Clamp((frame[i] / 32768f) * InputGain, -1f, 1f);

            var profile = GetTxProfile(txChannel);
            profile.TxEffect.Process(floatSamples);

            // Cosmetic "encrypted radio" character on top of the real
            // AES-GCM encryption below -- purely a sound cue that this
            // transmission is on a passcode-protected net, independent of
            // the actual security mechanism.
            var net = txChannel.SelectedNet;
            if (!net.IsUnencrypted)
                profile.EncryptionEffect.Process(floatSamples);

            for (int i = 0; i < FrameSize; i++)
                frame[i] = (short)Math.Clamp(floatSamples[i] * 32767f, short.MinValue, short.MaxValue);

            var encoded = _secureCodec.EncodeAndEncrypt(frame, net);

            bool isFirst = state.Sequence == 0;
            state.Sequence++;

            RaiseAudioCaptured(new CapturedAudioEventArgs
            {
                Packet = new AudioPacket
                {
                    Frequency = txChannel.Frequency,
                    Sequence = state.Sequence,
                    IsTransmissionStart = isFirst,
                    IsTransmissionEnd = false,
                    IsEncrypted = encoded.IsEncrypted,
                    NetIdHash = encoded.NetIdHash,
                    Nonce = encoded.Nonce,
                    Tag = encoded.Tag,
                    SenderName = Callsign,
                    RadioName = txChannel.Name,
                    Payload = encoded.Payload
                }
            });
        }

        /// Called by the networking layer when an audio packet arrives from another client.
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

                    if (!PacketMatchesChannelForReceiveControl(ch, packet)) continue;
                    rxState.LastRemotePacketUtc = DateTime.UtcNow;

                    bool remoteEndControlProcessed = false;
                    bool remoteEndWasAcceptedSender = false;
                    if (packet.IsTransmissionEnd)
                    {
                        remoteEndWasAcceptedSender = rxState.Interference.ShouldAcceptAudioFrom(packet.ClientId);
                        bool deferHudEndUntilAudioDrains = ShouldDeferRemoteHudEndUntilAudioDrains(
                            remoteEndWasAcceptedSender,
                            IsTransmitting(ch),
                            rxState.HasAudibleReceiveInFlight);

                        rxState.TalkOver.ObserveRemoteTransmissionEnd(packet.ClientId);
                        rxState.Interference.ObserveTransmissionEnd(packet.ClientId);
                        if (!rxState.Interference.HasInterference)
                            rxState.CollisionDestruction.Reset();
                        if (!deferHudEndUntilAudioDrains)
                            EndRemoteReceiveHud(ch, rxState, packet.ClientId.ToString());
                        remoteEndControlProcessed = true;

                        // Empty end packets are control-plane only, but they are
                        // also the data-plane signal that no more frames are coming.
                        // Feed them into jitter so RX audio/HUD end together when
                        // buffered audio drains, instead of leaving jitter to coast
                        // on packet-loss concealment after the UI already hid RX.
                        if (packet.Payload.Length == 0)
                        {
                            if (deferHudEndUntilAudioDrains)
                                jitter.OnFrameReceived(packet.Sequence, Array.Empty<byte>(), isStart: false, isEnd: true);
                            continue;
                        }
                    }

                    byte[]? opusFrame;
                    var net = ch.SelectedNet;

                    if (net.IsUnencrypted)
                    {
                        // This radio has no passcode set -- it has no key to
                        // decrypt anything with, so only open/unencrypted
                        // traffic makes sense to accept.
                        opusFrame = packet.IsEncrypted ? null : packet.Payload;
                    }
                    else
                    {
                        // This radio HAS a passcode set: it should only ever
                        // hear traffic encrypted under that exact passcode.
                        // Both open/unencrypted traffic AND traffic encrypted
                        // under a DIFFERENT passcode are correctly treated as
                        // unheard here -- setting a passcode "locks" this radio
                        // to that one net, rather than merely adding decryption
                        // capability on top of still hearing everything open.
                        opusFrame = (packet.IsEncrypted && NetIdHashesEqual(net.NetIdHash, packet.NetIdHash))
                            ? _secureCodec.DecryptToOpusFrame(packet.Payload, packet.Nonce, packet.Tag, net.Key!)
                            : null;
                    }

                    // opusFrame is null if this radio's current passcode doesn't
                    // match whatever (if anything) encrypted this packet, or the
                    // packet failed AES-GCM authentication. Either way, treat
                    // the transmission as never having been received at all: no
                    // jitter buffer interaction, no HUD popup for traffic we
                    // can't actually hear, and no local interference cue for a
                    // net this receiver is not tuned/unlocked to hear.
                    if (opusFrame == null) continue;

                    var remoteClientId = packet.ClientId.ToString();
                    if (packet.IsTransmissionStart)
                        StartRemoteReceiveHud(ch, rxState, remoteClientId, packet.SenderName);

                    if (RadioReceiveMute.ShouldMuteReceivedAudio(IsTransmitting(ch)))
                    {
                        // We are transmitting, so do not feed remote audio into
                        // the jitter/playout path. Still update the RX HUD and
                        // talk-over state from packet start/end flags so the
                        // operator can see who is keyed up on-frequency.
                        if (packet.IsTransmissionStart || (packet.IsTransmissionEnd && !remoteEndControlProcessed))
                        {
                            ProcessMutedRemoteReceiveControl(
                                ch,
                                rxState,
                                packet,
                                (channel, callsign, clientId) => StartRemoteReceiveHud(channel, rxState, clientId, callsign),
                                (channel, callsign, clientId) => EndRemoteReceiveHud(channel, rxState, clientId));
                        }

                        continue;
                    }

                    bool forceJitterStart = false;
                    if (packet.IsTransmissionStart)
                    {
                        rxState.TalkOver.ObserveRemoteTransmissionStart(packet.ClientId);

                        var decision = rxState.Interference.ObserveTransmissionStart(packet.ClientId);
                        if (!decision.AcceptAudio) continue;
                    }
                    else if (!remoteEndWasAcceptedSender && !rxState.Interference.ShouldAcceptAudioFrom(packet.ClientId))
                    {
                        bool canReacquireMutedTalkover = !packet.IsTransmissionEnd
                            && !rxState.Interference.HasPrimarySender
                            && rxState.TalkOver.IsRemoteTransmitting(packet.ClientId);
                        if (canReacquireMutedTalkover)
                        {
                            var decision = rxState.Interference.ObserveMidStreamTransmission(packet.ClientId);
                            if (!decision.AcceptAudio) continue;
                            ReacquireRemoteReceiveFromMidStream(rxState, packet, entry.buffer);
                            forceJitterStart = true;
                        }
                        else
                        {
                            if (packet.IsTransmissionEnd && !remoteEndControlProcessed)
                            {
                                rxState.TalkOver.ObserveRemoteTransmissionEnd(packet.ClientId);
                                rxState.Interference.ObserveTransmissionEnd(packet.ClientId);
                                if (!rxState.Interference.HasInterference)
                                    rxState.CollisionDestruction.Reset();
                                EndRemoteReceiveHud(ch, rxState, packet.ClientId.ToString());
                            }
                            continue;
                        }
                    }

                    entry.vol.Volume = BoostVolume(ch.Volume);
                    entry.pan.Pan = EarToPan(ch.Ear);

                    rxState.HasAudibleReceiveInFlight = true;
                    jitter.OnFrameReceived(packet.Sequence, opusFrame, packet.IsTransmissionStart, packet.IsTransmissionEnd, forceJitterStart);

                    if (packet.IsTransmissionEnd && !remoteEndControlProcessed)
                    {
                        rxState.TalkOver.ObserveRemoteTransmissionEnd(packet.ClientId);
                        rxState.Interference.ObserveTransmissionEnd(packet.ClientId);
                        if (!rxState.Interference.HasInterference)
                            rxState.CollisionDestruction.Reset();
                    }

                    if (packet.IsTransmissionStart)
                    {
                        rxState.PendingRemoteClientId = packet.ClientId.ToString();
                        rxState.PendingRemoteCallsign = packet.SenderName;
                    }
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

        internal static bool ShouldFallbackClearStaleRemoteHud(
            bool isReceiveHudActive,
            bool localTransmitting,
            bool hasAudibleReceiveInFlight,
            bool isReceivingActive,
            DateTime? lastRemotePacketUtc,
            DateTime nowUtc,
            TimeSpan idleTimeout)
        {
            if (!isReceiveHudActive) return false;
            if (localTransmitting) return false;
            if (isReceivingActive) return false;
            if (lastRemotePacketUtc == null) return false;

            return nowUtc - lastRemotePacketUtc.Value >= idleTimeout;
        }

        internal static void ReacquireRemoteReceiveFromMidStream(
            RxState rxState,
            AudioPacket packet,
            BufferedWaveProvider? receiveBuffer = null)
        {
            rxState.PendingRemoteClientId = packet.ClientId.ToString();
            rxState.PendingRemoteCallsign = packet.SenderName;
            rxState.CollisionDestruction.Reset();

            // A mid-stream reacquire is effectively tuning to a fresh receiver
            // after a collision/talkover episode. The jitter buffer already
            // drops stale decoder history via forceStart; clear any already
            // queued collided PCM too so the remaining sender resumes cleanly
            // instead of playing through old crackled audio first.
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

                    QueueTalkOverWarningFrame(ch, rxState);

                    var result = jitter.Tick();

                    if (result.IsFirstFrame)
                    {
                        rxState.IsReceivingActive = true;
                        StartRemoteReceiveHud(ch, rxState, rxState.PendingRemoteClientId, rxState.PendingRemoteCallsign);
                        var startClick = FloatsToPcm(ScaleVolume(SoundLibrary.RxStart, OutputClickVolume));
                        entry.buffer.AddSamples(startClick, 0, startClick.Length);
                    }

                    if (rxState.IsReceivingActive)
                    {
                        var profile = GetRxProfile(ch, rxState);
                        var frame = new float[FrameSize];

                        if (result.Pcm != null)
                        {
                            for (int i = 0; i < FrameSize && i < result.Pcm.Length; i++)
                                frame[i] = result.Pcm[i] / 32768f;
                            profile.RxEffect.Process(frame);
                        }

                        MixInBandNoise(ch, rxState, profile, frame);
                        MixCoChannelInterference(ch, rxState, frame);

                        var bytes = FloatsToPcm(frame);
                        entry.buffer.AddSamples(bytes, 0, bytes.Length);
                    }

                    // End click is queued right after the last real decoded
                    // frame's bytes, in the same buffer, so it plays exactly when
                    // that audio finishes -- matching whatever the PTT release
                    // delay actually was, instead of firing the instant the (already
                    // delayed) end-of-transmission packet showed up over the wire.
                    if (result.IsLastFrame)
                    {
                        rxState.IsReceivingActive = false;
                        rxState.TalkOverWarningCue.Reset();
                        rxState.HasAudibleReceiveInFlight = false;
                        var endClick = FloatsToPcm(ScaleVolume(SoundLibrary.RxEnd, OutputClickVolume));
                        entry.buffer.AddSamples(endClick, 0, endClick.Length);
                        if (!rxState.TalkOver.HasRemoteTransmitters)
                        {
                            EndAllRemoteReceiveHuds(ch, rxState, (_, remoteCallsign, remoteClientId) =>
                                RaiseRemoteTransmissionEnded(ch, remoteCallsign, remoteClientId));
                        }
                        else
                        {
                            EndRemoteReceiveHud(ch, rxState);
                        }
                    }

                    if (ShouldFallbackClearStaleRemoteHud(
                        rxState.IsReceiveHudActive,
                        IsTransmitting(ch),
                        rxState.HasAudibleReceiveInFlight,
                        rxState.IsReceivingActive,
                        rxState.LastRemotePacketUtc,
                        DateTime.UtcNow,
                        StaleRemoteHudFallbackTimeout))
                    {
                        jitter.Reset();
                        rxState.TalkOver.ClearRemoteTransmitters();
                        rxState.Interference.Reset();
                        rxState.CollisionDestruction.Reset();
                        rxState.IsReceivingActive = false;
                        rxState.HasAudibleReceiveInFlight = false;
                        EndAllRemoteReceiveHuds(ch, rxState, (_, remoteCallsign, remoteClientId) =>
                            RaiseRemoteTransmissionEnded(ch, remoteCallsign, remoteClientId));
                    }
                }

            }
        }

        /// Mixes the next slice of that band's looping recorded
        /// static bed into the frame at the profile's tuned noise level,
        /// advancing (and wrapping) a per-channel read cursor so consecutive
        /// transmissions continue from wherever the loop last left off
        /// rather than restarting identically every time.
        private void MixInBandNoise(RadioChannel channel, RxState rxState, RadioEffectProfile profile, float[] frame)
        {
            if (channel.IsIntercom) return; // no band static on a clean intercom

            var band = RadioBandExtensions.FromFrequencyMHz(channel.Frequency);
            var loop = SoundLibrary.GetBandNoiseLoop(band);
            if (loop.Length == 0) return;

            int pos = rxState.NoiseLoopPosition;
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = Math.Clamp(frame[i] + loop[pos] * profile.NoiseGainLinear, -1f, 1f);
                pos++;
                if (pos >= loop.Length) pos = 0;
            }
            rxState.NoiseLoopPosition = pos;
        }

        /// Destructively damages the captured signal while another
        /// audible transmitter overlaps the same frequency. This avoids
        /// playing a constant recorded squelch loop for listeners; instead,
        /// the samples themselves get multipath cancellation, flutter/chop,
        /// scratch, whoosh, and hiss.
        private void MixCoChannelInterference(RadioChannel channel, RxState rxState, float[] frame)
        {
            if (channel.IsIntercom) return;
            rxState.CollisionDestruction.Process(frame, rxState.Interference.HasInterference);
        }

        /// Plays a beep centered (both ears), independent of any
        /// radio channel, when the client successfully connects to a server.
        public void PlayConnectedBeep()
        {
            lock (_stateLock)
            {
                var pcm = FloatsToPcm(SoundLibrary.BeepConnected);
                ReplaceBufferedSamples(_systemSoundBuffer, pcm);

            }
        }

        /// Plays a beep centered (both ears), independent of any
        /// radio channel, when the client disconnects from a server.
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
            lock (_stateLock)
            {
                _jitterTicker.Dispose();
                _waveIn?.StopRecording();
                _waveIn?.Dispose();
                _waveIn = null;
                _waveOut?.Stop();
                _waveOut?.Dispose();
                _waveOut = null;

            }
        }
    }
}
