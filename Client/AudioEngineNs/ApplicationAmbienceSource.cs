using System;
using System.Threading;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Keeps the selected application capture alive and exposes live TX ambience samples.
    /// </summary>
    internal sealed class ApplicationAmbienceSource : IDisposable
    {
        private readonly object _gate = new();
        private readonly ApplicationAmbienceProcessor _processor = new();
        private readonly System.Threading.Timer _monitor;
        private ApplicationLoopbackCapture? _capture;
        private string? _executablePath;
        private string _processName = string.Empty;
        private int? _preferredProcessId;
        private string? _lastError;
        private int _monitoring;
        private bool _disposed;

        public ApplicationAmbienceSource()
        {
            _monitor = new System.Threading.Timer(_ => MonitorCapture(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public event Action<string>? CaptureFailed;

        public void SetTarget(ApplicationAudioTarget? target)
        {
            ApplicationLoopbackCapture? oldCapture;
            lock (_gate)
            {
                oldCapture = _capture;
                _capture = null;
                _executablePath = target?.ExecutablePath;
                _processName = target?.ProcessName ?? string.Empty;
                _preferredProcessId = target?.ProcessId;
                _lastError = null;
                _processor.ResetAll();
                _monitor.Change(target == null ? Timeout.Infinite : 0, target == null ? Timeout.Infinite : 2000);
            }

            oldCapture?.Dispose();
        }

        public float[] ReadSamples(int count) => _processor.ReadSamples(count);

        public void ResetTransmissionBuffer() => _processor.ResetTransmissionBuffer();

        private void MonitorCapture()
        {
            if (Interlocked.Exchange(ref _monitoring, 1) != 0) return;
            try
            {
                try { EnsureCapture(); }
                catch (Exception ex) { ReportFailure(ex.Message); }
            }
            finally
            {
                Volatile.Write(ref _monitoring, 0);
            }
        }

        private void EnsureCapture()
        {
            if (_disposed || !ApplicationAudioEnumerator.IsProcessLoopbackSupported) return;

            string? executablePath;
            string processName;
            int? preferredProcessId;
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(_processName)) return;
                executablePath = _executablePath;
                processName = _processName;
                preferredProcessId = _preferredProcessId;
            }

            var target = ApplicationAudioEnumerator.FindRunningApplication(
                executablePath,
                processName,
                preferredProcessId);
            if (target == null) return;

            ApplicationLoopbackCapture? oldCapture;
            lock (_gate)
            {
                if (_capture?.IsRunning == true && _capture.ProcessId == target.ProcessId) return;
                oldCapture = _capture;
                _capture = null;
            }

            oldCapture?.Dispose();
            if (oldCapture != null) _processor.ResetAll();

            var capture = new ApplicationLoopbackCapture(target.ProcessId);
            capture.DataAvailable += _processor.WritePcm16Stereo;
            capture.Stopped += error => OnCaptureStopped(capture, error);

            lock (_gate)
            {
                if (_disposed || _capture != null ||
                    !ApplicationAudioEnumerator.Matches(target, _executablePath, _processName))
                {
                    capture.Dispose();
                    return;
                }

                _capture = capture;
                _preferredProcessId = target.ProcessId;
            }

            try
            {
                capture.Start();
            }
            catch (Exception ex)
            {
                OnCaptureStopped(capture, ex);
            }
        }

        private void OnCaptureStopped(ApplicationLoopbackCapture capture, Exception? error)
        {
            bool report = false;
            bool wasCurrent;
            lock (_gate)
            {
                wasCurrent = ReferenceEquals(_capture, capture);
                if (wasCurrent) _capture = null;
                if (wasCurrent && error != null &&
                    !string.Equals(_lastError, error.Message, StringComparison.Ordinal))
                {
                    _lastError = error.Message;
                    report = true;
                }
            }

            if (wasCurrent) _processor.ResetAll();

            if (report)
            {
                try { CaptureFailed?.Invoke(error!.Message); }
                catch { }
            }

            capture.Dispose();
        }

        private void ReportFailure(string message)
        {
            bool report;
            lock (_gate)
            {
                report = !_disposed && !string.Equals(_lastError, message, StringComparison.Ordinal);
                if (report) _lastError = message;
            }

            if (!report) return;
            try { CaptureFailed?.Invoke(message); }
            catch { }
        }

        public void Dispose()
        {
            ApplicationLoopbackCapture? capture;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _monitor.Change(Timeout.Infinite, Timeout.Infinite);
                capture = _capture;
                _capture = null;
            }

            capture?.Dispose();
            _monitor.Dispose();
            _processor.ResetAll();
        }
    }
}
