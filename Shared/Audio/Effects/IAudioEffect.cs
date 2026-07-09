namespace RadioRelay.Shared.Audio.Effects
{
    /// 
    /// One stage in a radio audio effect chain, modeled on the DCS-SRS
    /// effect-graph schema ($type: "filters"/"saturation"/"sidechainCompressor"/
    /// "gain"/"chain") -- each stage processes a buffer of mono float samples
    /// (range roughly -1..1) in place, and stages are composed via ChainEffect
    /// to build up a full radio voice character from simple, independently
    /// tunable building blocks rather than one big bespoke DSP function.
    /// 
    public interface IAudioEffect
    {
        void Process(float[] samples);
    }
}
