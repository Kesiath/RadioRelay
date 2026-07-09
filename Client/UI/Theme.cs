using System.Drawing;
using System.Drawing.Drawing2D;

namespace RadioRelay.Client.UI
{
    /// 
    /// Shared dark instrument-style palette/fonts used by the main window and
    /// transmission HUD.
    /// 
    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(11, 13, 15);
        public static readonly Color CardBackground = Color.FromArgb(20, 24, 27);
        public static readonly Color RaisedBackground = Color.FromArgb(25, 30, 34);
        public static readonly Color FieldBackground = Color.FromArgb(11, 13, 15);
        public static readonly Color Border = Color.FromArgb(38, 45, 49);
        public static readonly Color SoftBorder = Color.FromArgb(29, 34, 38);
        public static readonly Color Text = Color.FromArgb(215, 222, 224);
        public static readonly Color MutedText = Color.FromArgb(124, 136, 144);
        public static readonly Color FaintText = Color.FromArgb(77, 86, 92);
        public static readonly Color AccentGreen = Color.FromArgb(111, 227, 196);
        public static readonly Color AccentRed = Color.FromArgb(255, 106, 57);
        public static readonly Color AccentOrange = Color.FromArgb(255, 106, 57);
        public static readonly Color TealDim = Color.FromArgb(47, 88, 80);
        public static readonly Color AmberDim = Color.FromArgb(92, 51, 34);

        public static readonly Font TitleFont = new("IBM Plex Mono", 8.75f, FontStyle.Bold);
        public static readonly Font RadioTitleFont = new("IBM Plex Mono", 11.25f, FontStyle.Bold);
        public static readonly Font BodyFont = new("Inter", 9f);
        public static readonly Font MonoFont = new("IBM Plex Mono", 9f);
        public static readonly Font ReadoutFont = new("IBM Plex Mono", 22f, FontStyle.Regular);
        public static readonly Font SmallMonoFont = new("IBM Plex Mono", 7.5f, FontStyle.Regular);

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
