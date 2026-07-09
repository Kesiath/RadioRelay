namespace RadioRelay.Client.UI
{
    public static class MainFormLayoutPolicy
    {
        public const int HorizontalMargin = 12;
        public const int MaxContentWidth = 460;
        public const int MinimumContentWidth = 460;
        public const int MinimumWindowWidth = MinimumContentWidth + HorizontalMargin * 2 + 24;
        public const int CompactBreakpoint = 650;
        public const int ConnectionStripHeight = 70;
        public const int SetupStripHeight = 132;
        public const int OperationsStripHeight = 70;
        public const int RadioCardHeight = 192;
        public const int RadioActivityBadgeWidth = 56;
        public const int RadioActivityBadgeColumnWidth = 84;
        public const int LogHeight = 70;

        public static int ContentWidthFor(int clientWidth)
        {
            var available = Math.Max(0, clientWidth - HorizontalMargin * 2);
            return Math.Clamp(available, MinimumContentWidth, MaxContentWidth);
        }

        public static bool UseCompactRadioGrid(int contentWidth) => contentWidth < CompactBreakpoint;

        public static int EstimatedMainPageHeight(int radioCount) =>
            32 + ConnectionStripHeight + SetupStripHeight + OperationsStripHeight +
            Math.Max(0, radioCount) * (RadioCardHeight + 8) + LogHeight + HorizontalMargin * 2;
    }
}
