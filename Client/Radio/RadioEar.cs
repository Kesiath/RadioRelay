namespace RadioRelay.Client.Radio
{
    /// Which speaker(s)/ear(s) a radio's received audio is routed
    /// to. Replaces the old "Listen" toggle -- since audio is now stereo,
    /// this doubles as spatial separation between radios (e.g. Radio 1 in
    /// your left ear, Radio 2 in your right) as well as a mute-by-omission
    /// if you really want a radio silent (set its Volume to 0 instead).
    public enum RadioEar
    {
        Left,
        Right,
        Both
    }
}
