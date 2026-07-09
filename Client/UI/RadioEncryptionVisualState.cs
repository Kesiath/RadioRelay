using System.Drawing;

namespace RadioRelay.Client.UI
{
    internal readonly record struct RadioEncryptionVisualState(string Text, Color ForeColor)
    {
        public static RadioEncryptionVisualState ForPasscode(string? passcode)
        {
            var encrypted = !string.IsNullOrWhiteSpace(passcode);
            return new RadioEncryptionVisualState(
                encrypted ? "ENCRYPTED" : "OPEN",
                encrypted ? Theme.AccentGreen : Theme.MutedText);
        }
    }
}
