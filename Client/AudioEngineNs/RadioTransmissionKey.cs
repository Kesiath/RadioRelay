using System;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Client.AudioEngineNs
{
    /// <summary>
    /// Identifies one keyed transmission; a zero ID falls back to sender identity.
    /// </summary>
    internal readonly record struct RadioTransmissionKey(Guid ClientId, ulong TransmissionId)
    {
        public static RadioTransmissionKey FromPacket(AudioPacket packet) =>
            new(packet.ClientId, packet.TransmissionId);

        public string HudId => TransmissionId == 0
            ? ClientId.ToString()
            : $"{ClientId}/{TransmissionId:X16}";
    }
}
