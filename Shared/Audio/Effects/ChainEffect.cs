namespace RadioRelay.Shared.Audio.Effects
{
    /// DCS-SRS "$type": "chain" (and also used for the "filters"
    /// grouping, which is structurally identical -- an ordered sequence of
    /// sub-effects applied one after another).
    public class ChainEffect : IAudioEffect
    {
        private readonly IAudioEffect[] _effects;

        public ChainEffect(params IAudioEffect[] effects)
        {
            _effects = effects;
        }

        public void Process(float[] samples)
        {
            foreach (var effect in _effects)
                effect.Process(samples);
        }
    }
}
