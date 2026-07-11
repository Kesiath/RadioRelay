using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace RadioRelay.Client.UI
{
    /// <summary>Shared modern dark palette and typography for the client UI.</summary>
    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(9, 12, 16);
        public static readonly Color CardBackground = Color.FromArgb(17, 22, 28);
        public static readonly Color RaisedBackground = Color.FromArgb(24, 31, 39);
        public static readonly Color FieldBackground = Color.FromArgb(12, 16, 21);
        public static readonly Color Border = Color.FromArgb(43, 53, 64);
        public static readonly Color SoftBorder = Color.FromArgb(31, 39, 48);
        public static readonly Color Text = Color.FromArgb(232, 237, 241);
        public static readonly Color MutedText = Color.FromArgb(148, 160, 171);
        public static readonly Color FaintText = Color.FromArgb(116, 130, 143);
        public static readonly Color HeaderText = Color.FromArgb(142, 156, 169);
        public static readonly Color DisabledText = Color.FromArgb(82, 92, 101);
        public static readonly Color DisabledFill = Color.FromArgb(26, 31, 36);
        public static readonly Color SliderTrack = Color.FromArgb(45, 55, 65);
        public static readonly Color SliderThumb = Color.FromArgb(210, 220, 227);
        public static readonly Color AccentGreen = Color.FromArgb(72, 220, 181);
        public static readonly Color AccentBlue = Color.FromArgb(85, 159, 255);
        public static readonly Color AccentRed = Color.FromArgb(255, 102, 108);
        public static readonly Color AccentOrange = Color.FromArgb(255, 167, 82);
        public static readonly Color TxUsername = Color.FromArgb(255, 205, 132);
        public static readonly Color RxUsername = Color.FromArgb(135, 245, 214);
        public static readonly Color TealDim = Color.FromArgb(34, 80, 72);
        public static readonly Color AmberDim = Color.FromArgb(88, 58, 31);

        // System fonts keep the UI predictable on clean Windows installs.
        public static readonly Font TitleFont = new("Segoe UI", 11f, FontStyle.Bold);
        public static readonly Font SectionTitleFont = new("Segoe UI", 10f, FontStyle.Bold);
        public static readonly Font RadioTitleFont = new("Segoe UI", 16.5f, FontStyle.Bold);
        public static readonly Font BodyFont = new("Segoe UI", 9.25f, FontStyle.Regular);
        public static readonly Font ButtonFont = new("Segoe UI", 8.75f, FontStyle.Bold);
        public static readonly Font MonoFont = new("Consolas", 9f, FontStyle.Regular);
        public static readonly Font ReadoutFont = new("Consolas", 22f, FontStyle.Regular);
        public static readonly Font SmallMonoFont = new("Consolas", 7.75f, FontStyle.Regular);

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return new GraphicsPath();

            // Rectangle.Right and Rectangle.Bottom are exclusive coordinates.
            // Using them directly places the right/bottom path one pixel outside
            // the control's drawable area, so GDI+ clips those border segments.
            // Keep every point on an inclusive pixel coordinate instead.
            float left = bounds.Left;
            float top = bounds.Top;
            float right = bounds.Right - 1f;
            float bottom = bounds.Bottom - 1f;
            float availableWidth = Math.Max(0f, right - left);
            float availableHeight = Math.Max(0f, bottom - top);

            radius = Math.Clamp(radius, 0, (int)Math.Floor(Math.Min(availableWidth, availableHeight) / 2f));
            var path = new GraphicsPath();

            if (radius == 0)
            {
                path.AddRectangle(RectangleF.FromLTRB(left, top, right, bottom));
                path.CloseFigure();
                return path;
            }

            float diameter = radius * 2f;
            path.StartFigure();
            path.AddArc(left, top, diameter, diameter, 180f, 90f);
            path.AddLine(left + radius, top, right - radius, top);
            path.AddArc(right - diameter, top, diameter, diameter, 270f, 90f);
            path.AddLine(right, top + radius, right, bottom - radius);
            path.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddLine(right - radius, bottom, left + radius, bottom);
            path.AddArc(left, bottom - diameter, diameter, diameter, 90f, 90f);
            path.AddLine(left, bottom - radius, left, top + radius);
            path.CloseFigure();
            return path;
        }

        public static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Clamp(amount, 0f, 1f);
            return Color.FromArgb(
                (int)Math.Round(from.A + (to.A - from.A) * amount),
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }
    }
}
