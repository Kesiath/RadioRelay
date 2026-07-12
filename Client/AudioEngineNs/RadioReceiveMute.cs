namespace RadioRelay.Client.AudioEngineNs
{
    internal static class RadioReceiveMute
    {
        public static bool ShouldMuteReceivedAudio(bool localTransmitting) => localTransmitting;

        public static bool IsReceiveDisabled(float volume) => volume <= 0f;

        public static bool CanStartTransmission(float volume) => volume > 0f;
    }
}
