namespace RadioRelay.Client.UI
{
    /// <summary>Shared dimensions and layout budgets for the fixed-width main window.</summary>
    public static class MainFormLayoutPolicy
    {
        public const int HorizontalMargin = 18;
        public const int FixedWindowWidth = 820;
        public const int FixedWindowHeight = 860;
        public const int MinimumWindowHeight = 560;
        public const int MaximumWindowHeight = short.MaxValue;
        public const int MaxContentWidth = 760;
        public const int ConnectionStripHeight = 248;
        public const int SetupStripHeight = 136;
        public const int OperationsStripHeight = 92;
        public const int RadioCardHeight = 176;
        public const int RadioTitleColumnWidth = 136;
        public const int RadioActivityBadgeWidth = 70;
        public const int RadioActivityBadgeColumnWidth = 70;
        public const int LogHeight = 164;

        public static int EstimatedMainPageHeight(int radioCount) =>
            58 + ConnectionStripHeight + OperationsStripHeight + 46 +
            Math.Max(0, radioCount) * (RadioCardHeight + 10) + LogHeight + HorizontalMargin * 2;
    }
}
