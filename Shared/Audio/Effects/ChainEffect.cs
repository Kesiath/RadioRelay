namespace RadioRelay.Shared.Audio.Effects
{
    /// <summary>
    /// Ordered sequence of independently resettable audio effects.
    /// </summary>
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

        public void Reset()
        {
            foreach (var effect in _effects)
                effect.Reset();
        }
    }
}
