namespace RadioRelay.Client
{
    public readonly record struct RadioControlLock(
        bool IsLocked,
        bool CanEditName,
        bool CanEditFrequency,
        bool CanEditPasscode,
        bool CanChangePttBinding,
        bool CanAdjustVolume,
        string ToggleButtonText)
    {
        public bool ProtectedControlsEnabled => !IsLocked;

        public static RadioControlLock For(bool locked) => new(
            IsLocked: locked,
            CanEditName: !locked,
            CanEditFrequency: !locked,
            CanEditPasscode: !locked,
            CanChangePttBinding: !locked,
            CanAdjustVolume: true,
            ToggleButtonText: locked ? "Unlock Controls" : "Lock Controls");
    }
}
