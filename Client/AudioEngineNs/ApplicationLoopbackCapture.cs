using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Captures the render streams produced by one Windows process tree.
    /// </summary>
    internal sealed class ApplicationLoopbackCapture :
        IActivateAudioInterfaceCompletionHandler,
        ApplicationLoopbackCapture.IAgileObject,
        IDisposable
    {
        private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
        private const ushort VariantBlob = 65;
        private const int CoInitMultithreaded = 0;
        private const int RpcChangedMode = unchecked((int)0x80010106);
        private static readonly Guid AudioClientInterfaceId =
            new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

        private readonly int _processId;
        private readonly TaskCompletionSource<IAudioClient> _activation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AutoResetEvent _sampleReady = new(false);
        private readonly ManualResetEvent _stopRequested = new(false);
        private readonly CancellationTokenSource _cancellation = new();
        private Thread? _thread;
        private bool _disposed;

        public ApplicationLoopbackCapture(int processId)
        {
            if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            _processId = processId;
        }

        public event Action<byte[]>? DataAvailable;
        public event Action<Exception?>? Stopped;

        public int ProcessId => _processId;
        public bool IsRunning => _thread?.IsAlive == true && !_cancellation.IsCancellationRequested;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread != null) throw new InvalidOperationException("Application capture has already been started.");

            _thread = new Thread(CaptureThread)
            {
                IsBackground = true,
                Name = $"RadioRelay application capture {_processId}",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }

        public void Stop()
        {
            _cancellation.Cancel();
            _stopRequested.Set();
            var thread = _thread;
            if (thread?.IsAlive == true && thread != Thread.CurrentThread)
                thread.Join(TimeSpan.FromSeconds(2));
        }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out int result, out object activatedInterface);
                Marshal.ThrowExceptionForHR(result);
                _activation.TrySetResult((IAudioClient)activatedInterface);
            }
            catch (Exception ex)
            {
                _activation.TrySetException(ex);
            }
        }

        private void CaptureThread()
        {
            Exception? failure = null;
            int coInitializeResult = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            bool uninitializeCom = coInitializeResult >= 0;
            try
            {
                if (coInitializeResult < 0 && coInitializeResult != RpcChangedMode)
                    Marshal.ThrowExceptionForHR(coInitializeResult);

                RunCapture();
            }
            catch (Exception ex) when (!_cancellation.IsCancellationRequested)
            {
                failure = ex;
            }
            finally
            {
                if (uninitializeCom) CoUninitialize();
                try { Stopped?.Invoke(failure); }
                catch { }
            }
        }

        private void RunCapture()
        {
            using var audioClient = ActivateAudioClient();
            var format = new WaveFormat(
                ApplicationAmbienceProcessor.CaptureSampleRate,
                16,
                ApplicationAmbienceProcessor.CaptureChannels);
            audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback |
                AudioClientStreamFlags.EventCallback |
                AudioClientStreamFlags.AutoConvertPcm |
                AudioClientStreamFlags.SrcDefaultQuality,
                0,
                0,
                format,
                Guid.Empty);
            audioClient.SetEventHandle(_sampleReady.SafeWaitHandle.DangerousGetHandle());
            var captureClient = audioClient.AudioCaptureClient;

            audioClient.Start();
            try
            {
                var waits = new WaitHandle[] { _sampleReady, _stopRequested };
                while (!_cancellation.IsCancellationRequested)
                {
                    int signaled = WaitHandle.WaitAny(waits, 1000);
                    if (signaled == 1) break;
                    if (signaled == WaitHandle.WaitTimeout)
                    {
                        if (!IsTargetProcessRunning()) break;
                        continue;
                    }

                    DrainPackets(captureClient, format.BlockAlign);
                }
            }
            finally
            {
                try { audioClient.Stop(); }
                catch { }
            }
        }

        private AudioClient ActivateAudioClient()
        {
            var activation = new AudioClientActivationParameters
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParameters = new AudioClientProcessLoopbackParameters
                {
                    TargetProcessId = unchecked((uint)_processId),
                    ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree
                }
            };
            IntPtr activationBlob = Marshal.AllocCoTaskMem(Marshal.SizeOf<AudioClientActivationParameters>());
            IntPtr variantPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<PropVariant>());
            IActivateAudioInterfaceAsyncOperation? operation = null;
            try
            {
                Marshal.StructureToPtr(activation, activationBlob, false);
                var variant = new PropVariant
                {
                    VariantType = VariantBlob,
                    Blob = new Blob
                    {
                        Size = Marshal.SizeOf<AudioClientActivationParameters>(),
                        Data = activationBlob
                    }
                };
                Marshal.StructureToPtr(variant, variantPointer, false);

                ActivateAudioInterfaceAsync(
                    ProcessLoopbackDevice,
                    AudioClientInterfaceId,
                    variantPointer,
                    this,
                    out operation);

                try
                {
                    if (!_activation.Task.Wait(TimeSpan.FromSeconds(5), _cancellation.Token))
                        throw new TimeoutException("Windows did not activate application audio capture within five seconds.");
                }
                catch (AggregateException)
                {
                    var failedActivation = _activation.Task.GetAwaiter().GetResult();
                    return new AudioClient(failedActivation);
                }
                return new AudioClient(_activation.Task.GetAwaiter().GetResult());
            }
            finally
            {
                if (operation != null && Marshal.IsComObject(operation))
                    Marshal.ReleaseComObject(operation);
                Marshal.FreeCoTaskMem(variantPointer);
                Marshal.FreeCoTaskMem(activationBlob);
            }
        }

        private void DrainPackets(AudioCaptureClient captureClient, int blockAlign)
        {
            while (captureClient.GetNextPacketSize() > 0)
            {
                IntPtr buffer = captureClient.GetBuffer(out int frames, out var flags);
                try
                {
                    int byteCount = checked(frames * blockAlign);
                    var pcm = new byte[byteCount];
                    if ((flags & AudioClientBufferFlags.Silent) == 0 && buffer != IntPtr.Zero)
                        Marshal.Copy(buffer, pcm, 0, byteCount);
                    DataAvailable?.Invoke(pcm);
                }
                finally
                {
                    captureClient.ReleaseBuffer(frames);
                }
            }
        }

        private bool IsTargetProcessRunning()
        {
            try
            {
                using var process = Process.GetProcessById(_processId);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _cancellation.Dispose();
            _sampleReady.Dispose();
            _stopRequested.Dispose();
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        private static extern void ActivateAudioInterfaceAsync(
            [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [In] IntPtr activationParameters,
            [In] IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, int concurrencyModel);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        private enum AudioClientActivationType
        {
            ProcessLoopback = 1
        }

        private enum ProcessLoopbackMode
        {
            IncludeTargetProcessTree = 0
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientProcessLoopbackParameters
        {
            public uint TargetProcessId;
            public ProcessLoopbackMode ProcessLoopbackMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParameters
        {
            public AudioClientActivationType ActivationType;
            public AudioClientProcessLoopbackParameters ProcessLoopbackParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort VariantType;
            [FieldOffset(8)] public Blob Blob;
        }

        [ComImport]
        [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject
        {
        }
    }
}
