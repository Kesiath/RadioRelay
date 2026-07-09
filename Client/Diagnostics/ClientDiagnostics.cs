using System;
using RadioRelay.Shared.Diagnostics;

namespace RadioRelay.Client.Diagnostics
{
    public interface IClientDiagnostics
    {
        void LogLifecycle(string code, string message);
        void LogException(string code, string context, Exception exception);
    }

    public sealed class LocalLogClientDiagnostics : IClientDiagnostics
    {
        private readonly LocalLog _log;

        public LocalLogClientDiagnostics(LocalLog log)
        {
            _log = log;
        }

        public void LogLifecycle(string code, string message) => _log.LogLifecycle(code, message);

        public void LogException(string code, string context, Exception exception) => _log.LogException(code, context, exception);
    }

    public static class ClientDiagnostics
    {
        public static IClientDiagnostics? Current { get; set; }

        public static IClientDiagnostics CreateDefault() =>
            new LocalLogClientDiagnostics(new LocalLog(LocalLog.DefaultLogDirectory, "RadioRelay-client"));
    }
}
