using RadioRelay.Client.Radio;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Client
{
    public static class PresenceDisplay
    {
        public static int CountFor(RadioChannel channel, PresenceChannelCount[] counts)
        {
            var key = channel.SelectedNet.NetIdHash;
            foreach (var count in counts)
            {
                if (count.Matches(channel.Frequency, key)) return count.UserCount;
            }
            return 0;
        }

        public static string FormatCount(int count) => count == 1 ? "1 user" : $"{count} users";
    }
}
