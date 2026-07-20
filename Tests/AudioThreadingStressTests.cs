using System.Collections.Concurrent;
using RadioRelay.Client.AudioEngineNs;
using RadioRelay.Shared.Audio;

namespace RadioRelay.Tests;

public class AudioThreadingStressTests
{
    [Fact]
    public async Task AudioEngine_tolerates_concurrent_tx_rx_and_dispose_calls()
    {
        var channel = new RadioRelay.Client.Radio.RadioChannel { Name = "Radio", Frequency = 251.000f };
        var engine = new AudioEngine(new List<RadioRelay.Client.Radio.RadioChannel> { channel }, startAudioDevices: false);
        var encoder = new OpusCodec(AudioEngine.SampleRate);
        var frame = encoder.Encode(Tone(550f));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        var errors = new ConcurrentQueue<Exception>();

        Task Run(Action action) => Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                    action();
            }
            catch (ObjectDisposedException)
            {
                // Ignore disposal races after cancellation, but no other failures.
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
                cts.Cancel();
            }
        });

        ushort sequence = 0;
        var txTask = Run(() =>
        {
            engine.SetTransmitting(channel, true);
            engine.SetTransmitting(channel, false);
        });
        var rxTask = Run(() =>
        {
            var current = unchecked(sequence++);
            engine.OnAudioReceived(new RadioRelay.Shared.Protocol.AudioPacket
            {
                ClientId = Guid.NewGuid(),
                Frequency = channel.Frequency,
                Sequence = current,
                IsTransmissionStart = current % 25 == 0,
                IsTransmissionEnd = current % 25 == 24,
                SenderName = "Remote",
                RadioName = "Radio",
                Payload = frame
            });
        });

        await Task.Delay(100);
        engine.Dispose();
        cts.Cancel();
        await Task.WhenAll(txTask, rxTask).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }

    [Fact]
    public async Task JitterBuffer_tolerates_concurrent_network_timer_and_reset_calls()
    {
        var encoder = new OpusCodec(AudioEngine.SampleRate);
        var jitter = new JitterBuffer(new OpusCodec(AudioEngine.SampleRate));
        var frame = encoder.Encode(Tone(440f));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        var errors = new ConcurrentQueue<Exception>();

        Task Run(Action action) => Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                    action();
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
                cts.Cancel();
            }
        });

        ushort sequence = 0;
        var receiveTask = Run(() =>
        {
            var current = unchecked(sequence++);
            jitter.OnFrameReceived(current, frame, isStart: current % 25 == 0, isEnd: current % 25 == 24);
        });
        var timerTask = Run(() => jitter.Tick());
        var resetTask = Run(() => jitter.Reset());

        await Task.WhenAll(receiveTask, timerTask, resetTask).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }

    private static short[] Tone(float frequency)
    {
        var pcm = new short[AudioEngine.SampleRate / 50];
        for (int i = 0; i < pcm.Length; i++)
        {
            float sample = MathF.Sin(MathF.Tau * frequency * i / AudioEngine.SampleRate) * 0.35f;
            pcm[i] = (short)(sample * short.MaxValue);
        }

        return pcm;
    }
}
