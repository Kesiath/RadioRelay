using System;
using System.Runtime.InteropServices;

namespace RadioRelay.Client.Input
{
    /// 
    /// Global low-level mouse hook for mouse side buttons. Reports XBUTTON1
    /// and XBUTTON2 system-wide so mouse button 4/5 can be bound directly as
    /// PTT even while another app has focus.
    /// 
    public class PttMouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;
        private const int XBUTTON1 = 0x0001;
        private const int XBUTTON2 = 0x0002;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private readonly LowLevelMouseProc _proc;
        private IntPtr _hookId = IntPtr.Zero;

        public event Action<int>? ButtonDown;
        public event Action<int>? ButtonUp;

        public PttMouseHook()
        {
            _proc = HookCallback;
        }

        public void Start()
        {
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_XBUTTONDOWN || wParam == (IntPtr)WM_XBUTTONUP))
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int xButton = unchecked((short)((info.mouseData >> 16) & 0xFFFF));
                int button = xButton switch
                {
                    XBUTTON1 => MousePttButtons.XButton1,
                    XBUTTON2 => MousePttButtons.XButton2,
                    _ => 0
                };

                if (button != 0)
                {
                    if (wParam == (IntPtr)WM_XBUTTONDOWN) ButtonDown?.Invoke(button);
                    else ButtonUp?.Invoke(button);
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
                UnhookWindowsHookEx(_hookId);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
