namespace RadioRelay.Client.UI
{
    /// <summary>Shared dimensions and layout budgets for the fixed-size main window.</summary>
    public static class MainFormLayoutPolicy
    {
        public const int HorizontalMargin = 18;
        public const int FixedWindowWidth = 820;
        public const int FixedWindowHeight = 860;
        public const int MaxContentWidth = 760;
        public const int MinimumContentWidth = 680;
        public const int MinimumWindowWidth = MinimumContentWidth + HorizontalMargin * 2 + 28;
        public const int CompactBreakpoint = 720;
        public const int ConnectionStripHeight = 248;
        public const int SetupStripHeight = 136;
        public const int OperationsStripHeight = 92;
        public const int RadioCardHeight = 176;
        public const int RadioActivityBadgeWidth = 58;
        public const int RadioActivityBadgeColumnWidth = 70;
        public const int LogHeight = 164;

        public static int ContentWidthFor(int clientWidth)
        {
            var available = Math.Max(0, clientWidth - HorizontalMargin * 2);
            return Math.Clamp(available, MinimumContentWidth, MaxContentWidth);
        }

        public static bool UseCompactRadioGrid(int contentWidth) => contentWidth < CompactBreakpoint;

        public static int EstimatedMainPageHeight(int radioCount) =>
            58 + ConnectionStripHeight + OperationsStripHeight + 46 +
            Math.Max(0, radioCount) * (RadioCardHeight + 10) + LogHeight + HorizontalMargin * 2;
    }
}
