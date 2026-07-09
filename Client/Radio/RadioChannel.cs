using System.Drawing;

namespace RadioRelay.Client.Radio
{
    /// 
    /// One tunable "radio" (or intercom) in the client UI. Frequency, not
    /// channel name, is what determines who you talk to and hear. Passcode
    /// determines who among those on-frequency can actually decrypt you --
    /// leave it blank for an open/unencrypted radio. Each radio has its own
    /// independently-bindable push-to-talk triggers (two independent slots);
    /// whether you're "listening" on a radio is simply a function of its
    /// Volume.
    /// 
    public class RadioChannel
    {
        public string Name { get; set; } = "RADIO";
        public float Frequency { get; set; } = 251.000f;
        public float Volume { get; set; } = 1.0f; // 0..1 -- 0 is effectively "not listening"
        public bool IsIntercom { get; set; } = false;

        /// Blank = unencrypted/open. Non-blank = only other radios
        /// with this exact passcode typed in can decrypt this radio's
        /// traffic. Derived into an actual key via NetOption.FromPasscode.
        public string Passcode { get; set; } = "";

        /// Which ear(s) this radio's received audio plays in.
        public RadioEar Ear { get; set; } = RadioEar.Both;

        /// The accent color shown on this radio's on-screen
        /// transmission HUD chip, so multiple simultaneously-active radios
        /// are easy to tell apart at a glance.
        public Color HudColor { get; set; } = Color.FromArgb(90, 160, 235);

        /// Top-left position of this radio's HUD chip, in screen
        /// coordinates. Null means "not customized yet" -- the HUD falls
        /// back to an automatic cascading default position.
        public Point? HudPosition { get; set; }

        public NetOption SelectedNet => NetOption.FromPasscode(Passcode);
    }
}
