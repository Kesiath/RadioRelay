using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using RadioRelay.Client.Diagnostics;
using RadioRelay.Shared.Diagnostics;

namespace RadioRelay.Client
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var diagnostics = ClientDiagnostics.CreateDefault();
            ClientDiagnostics.Current = diagnostics;
            diagnostics.LogLifecycle(ErrorCodes.ClientAppStart, "client app starting");
            RegisterExceptionHandlers(diagnostics);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            diagnostics.LogLifecycle(ErrorCodes.ClientAppExit, "client app exiting");
        }

        private static void RegisterExceptionHandlers(IClientDiagnostics diagnostics)
        {
            Application.ThreadException += (_, e) =>
            {
                diagnostics.LogException(ErrorCodes.WinFormsThreadException, "WinForms thread exception", e.Exception);
                MessageBox.Show("RadioRelay hit an unexpected UI error. Details were written to the local log.", "RadioRelay", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    diagnostics.LogException(ErrorCodes.UnhandledAppDomainException, "unhandled AppDomain exception", ex);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                diagnostics.LogException(ErrorCodes.UnobservedTaskException, "unobserved task exception", e.Exception);
            };
        }
    }
}
