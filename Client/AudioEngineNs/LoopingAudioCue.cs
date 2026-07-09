using System;

namespace RadioRelay.Client.AudioEngineNs
{
    /// Small stateful loop reader for warning cues that need to last
    /// as long as a condition remains active, not merely for the length of one
    /// recorded asset.
    internal sealed class LoopingAudioCue
    {
        private readonly float[] _source;
        private int _position;

        public LoopingAudioCue(float[] source)
        {
            _source = source;
        }

        public float[] ReadFrame(int sampleCount, float gain)
        {
            var frame = new float[sampleCount];
            if (_source.Length == 0) return frame;

            for (int i = 0; i < sampleCount; i++)
            {
                frame[i] = Math.Clamp(_source[_position] * gain, -1f, 1f);
                _position++;
                if (_position >= _source.Length) _position = 0;
            }

            return frame;
        }

        public void Reset() => _position = 0;
    }
}
