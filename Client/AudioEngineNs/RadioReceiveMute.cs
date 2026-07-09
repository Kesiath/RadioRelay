namespace RadioRelay.Client.AudioEngineNs
{
    internal static class RadioReceiveMute
    {
        public static bool ShouldMuteReceivedAudio(bool localTransmitting) => localTransmitting;
    }
}
