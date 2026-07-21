using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Identifies one running application that can supply TX ambience.
    /// </summary>
    public sealed record ApplicationAudioTarget(
        int ProcessId,
        string ProcessName,
        string? ExecutablePath,
        string DisplayName,
        bool HasAudioSession = false,
        bool IsAudioActive = false);

    public static class ApplicationAudioEnumerator
    {
        public static bool IsProcessLoopbackSupported =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

        public static IReadOnlyList<ApplicationAudioTarget> GetRunningApplications()
        {
            if (!OperatingSystem.IsWindows()) return Array.Empty<ApplicationAudioTarget>();

            int ownProcessId = Environment.ProcessId;
            var applications = GetAudioSessionApplications(ownProcessId);
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == ownProcessId || process.HasExited) continue;

                        string title = process.MainWindowTitle?.Trim() ?? string.Empty;
                        if (title.Length == 0) continue;

                        applications.Add(CreateTarget(process, title));
                    }
                    catch
                    {
                        // Processes can exit or deny metadata access during enumeration.
                    }
                }
            }

            return SelectPreferredTargets(applications);
        }

        internal static IReadOnlyList<ApplicationAudioTarget> SelectPreferredTargets(
            IEnumerable<ApplicationAudioTarget> applications)
        {
            return applications
                .GroupBy(StableIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.IsAudioActive)
                    .ThenByDescending(item => item.HasAudioSession)
                    .ThenBy(item => item.ProcessId)
                    .First())
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ProcessId)
                .ToArray();
        }

        private static List<ApplicationAudioTarget> GetAudioSessionApplications(int ownProcessId)
        {
            var applications = new List<ApplicationAudioTarget>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(
                    DataFlow.Render,
                    DeviceState.Active))
                {
                    try
                    {
                        var sessionManager = device.AudioSessionManager;
                        try
                        {
                            sessionManager.RefreshSessions();
                            var sessions = sessionManager.Sessions;
                            for (int index = 0; index < sessions.Count; index++)
                            {
                                using var session = sessions[index];
                                if (session.IsSystemSoundsSession ||
                                    session.State == AudioSessionState.AudioSessionStateExpired)
                                {
                                    continue;
                                }

                                int processId = unchecked((int)session.GetProcessID);
                                if (processId <= 0 || processId == ownProcessId) continue;

                                try
                                {
                                    using var process = Process.GetProcessById(processId);
                                    if (process.HasExited) continue;
                                    applications.Add(CreateTarget(
                                        process,
                                        process.MainWindowTitle?.Trim(),
                                        hasAudioSession: true,
                                        isAudioActive: session.State == AudioSessionState.AudioSessionStateActive));
                                }
                                catch
                                {
                                    // Audio sessions can outlive their process briefly.
                                }
                            }
                        }
                        finally
                        {
                            sessionManager.Dispose();
                        }
                    }
                    catch
                    {
                        // One unavailable endpoint must not hide sessions on other endpoints.
                    }
                }
            }
            catch
            {
                // Visible applications remain available if Core Audio session discovery fails.
            }

            return applications;
        }

        private static ApplicationAudioTarget CreateTarget(
            Process process,
            string? windowTitle,
            bool hasAudioSession = false,
            bool isAudioActive = false)
        {
            string processName = process.ProcessName;
            string? executablePath = TryGetExecutablePath(process);
            string executableName = executablePath == null
                ? processName
                : Path.GetFileNameWithoutExtension(executablePath);
            string title = windowTitle?.Trim() ?? string.Empty;
            string displayName = title.Length == 0 ||
                string.Equals(title, executableName, StringComparison.OrdinalIgnoreCase)
                    ? executableName
                    : $"{title} ({executableName})";

            return new ApplicationAudioTarget(
                process.Id,
                processName,
                executablePath,
                displayName,
                hasAudioSession,
                isAudioActive);
        }

        internal static ApplicationAudioTarget? FindRunningApplication(
            string? executablePath,
            string processName,
            int? preferredProcessId = null)
        {
            var applications = GetRunningApplications();
            if (preferredProcessId.HasValue)
            {
                var preferred = applications.FirstOrDefault(item => item.ProcessId == preferredProcessId.Value);
                if (preferred != null && Matches(preferred, executablePath, processName))
                    return preferred;
            }

            return applications.FirstOrDefault(item => Matches(item, executablePath, processName));
        }

        internal static bool Matches(
            ApplicationAudioTarget target,
            string? executablePath,
            string processName)
        {
            if (!string.IsNullOrWhiteSpace(executablePath) &&
                !string.IsNullOrWhiteSpace(target.ExecutablePath))
            {
                return string.Equals(
                    target.ExecutablePath,
                    executablePath,
                    StringComparison.OrdinalIgnoreCase);
            }

            return !string.IsNullOrWhiteSpace(processName) &&
                string.Equals(target.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }

        private static string StableIdentity(ApplicationAudioTarget target) =>
            string.IsNullOrWhiteSpace(target.ExecutablePath)
                ? target.ProcessName
                : target.ExecutablePath;

        private static string? TryGetExecutablePath(Process process)
        {
            try { return process.MainModule?.FileName; }
            catch { return null; }
        }
    }
}
