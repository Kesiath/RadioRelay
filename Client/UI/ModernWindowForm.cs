using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// <summary>
    /// Frameless top-level window that retains native drag, resize, maximize,
    /// work-area, and system-menu behavior while drawing RadioRelay's chrome.
    /// </summary>
    public class ModernWindowForm : Form
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;
        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        private readonly Panel _contentHost;

        protected ModernWindowForm()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = true;
            MinimizeBox = true;
            SizeGripStyle = SizeGripStyle.Hide;
            Padding = new Padding(1);
            BackColor = Theme.Border;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            TitleBar = new ModernTitleBar();
            _contentHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            Controls.Add(_contentHost);
            Controls.Add(TitleBar);

            TitleBar.MinimizeRequested += (_, _) => WindowState = FormWindowState.Minimized;
            TitleBar.MaximizeRestoreRequested += (_, _) => ToggleMaximizeRestore();
            TitleBar.CloseRequested += (_, _) => Close();
        }

        protected Panel ContentHost => _contentHost;
        protected ModernTitleBar TitleBar { get; }
        protected bool CanResizeHorizontally { get; set; } = true;
        protected bool CanResizeVertically { get; set; } = true;

        protected int ResizeBorderThickness => Math.Max(6, DeviceDpi / 16);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryEnableRoundedCorners();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            TitleBar.SetWindowActive(true);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            TitleBar.SetWindowActive(false);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            TitleBar.SetWindowState(WindowState == FormWindowState.Maximized);
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result != HTCLIENT)
                    return;

                var screenPoint = UnpackScreenPoint(m.LParam);
                var clientPoint = PointToClient(screenPoint);

                if (WindowState == FormWindowState.Normal)
                {
                    var resizeHit = HitTestResizeBorder(
                        clientPoint,
                        ClientSize,
                        ResizeBorderThickness,
                        CanResizeHorizontally,
                        CanResizeVertically);
                    if (resizeHit != HTCLIENT)
                    {
                        m.Result = (IntPtr)resizeHit;
                        return;
                    }
                }

                var titlePoint = TitleBar.PointToClient(screenPoint);
                if (TitleBar.ClientRectangle.Contains(titlePoint) && !TitleBar.IsInteractiveAt(titlePoint))
                {
                    m.Result = (IntPtr)HTCAPTION;
                    return;
                }

                return;
            }

            if (m.Msg == WM_GETMINMAXINFO)
            {
                base.WndProc(ref m);
                ConstrainMaximizedBounds(m.LParam);
                return;
            }

            base.WndProc(ref m);
        }

        internal static int HitTestResizeBorder(
            Point point,
            Size clientSize,
            int thickness,
            bool allowHorizontal = true,
            bool allowVertical = true)
        {
            var left = allowHorizontal && point.X >= 0 && point.X < thickness;
            var right = allowHorizontal && point.X < clientSize.Width && point.X >= clientSize.Width - thickness;
            var top = allowVertical && point.Y >= 0 && point.Y < thickness;
            var bottom = allowVertical && point.Y < clientSize.Height && point.Y >= clientSize.Height - thickness;

            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
            return HTCLIENT;
        }

        private void ToggleMaximizeRestore()
        {
            if (!MaximizeBox)
                return;

            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        }

        private void TryEnableRoundedCorners()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return;

            try
            {
                var preference = DWMWCP_ROUND;
                _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private void ConstrainMaximizedBounds(IntPtr lParam)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return;

            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
                return;

            var work = monitorInfo.rcWork;
            var monitorBounds = monitorInfo.rcMonitor;
            info.ptMaxPosition.X = Math.Abs(work.Left - monitorBounds.Left);
            info.ptMaxPosition.Y = Math.Abs(work.Top - monitorBounds.Top);
            info.ptMaxSize.X = Math.Abs(work.Right - work.Left);
            info.ptMaxSize.Y = Math.Abs(work.Bottom - work.Top);
            Marshal.StructureToPtr(info, lParam, false);
        }

        private static Point UnpackScreenPoint(IntPtr lParam)
        {
            var value = lParam.ToInt64();
            return new Point(
                unchecked((short)(value & 0xFFFF)),
                unchecked((short)((value >> 16) & 0xFFFF)));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO monitorInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }
    }
}
