using System;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>
    /// Fixed pure transport delay for a scalar signal, quantised to the physics
    /// step. Push one sample per step, read the sample from
    /// <c>round(delaySeconds / dt)</c> steps ago. Used for actuator command
    /// latency and sensor latency.
    /// </summary>
    public sealed class DelayLine
    {
        private readonly double[] _buffer;
        private int _head;
        private readonly int _delaySteps;
        private readonly double _fill;

        public DelayLine(double delaySeconds, double dt, double initialValue = 0.0)
        {
            _delaySteps = Math.Max(0, (int)Math.Round(delaySeconds / dt));
            _buffer = new double[_delaySteps + 1];
            _fill = initialValue;
            Reset(initialValue);
        }

        public int DelaySteps => _delaySteps;

        public void Reset(double value)
        {
            for (int i = 0; i < _buffer.Length; i++) _buffer[i] = value;
            _head = 0;
        }

        public void Reset() => Reset(_fill);

        /// <summary>Copy the buffered samples of an identically-configured line.</summary>
        public void CopyStateFrom(DelayLine o)
        {
            Array.Copy(o._buffer, _buffer, _buffer.Length);
            _head = o._head;
        }

        /// <summary>Push the newest sample and return the delayed sample.</summary>
        public double Step(double newest)
        {
            _buffer[_head] = newest;
            _head = (_head + 1) % _buffer.Length;
            return _buffer[_head]; // oldest slot, == sample from _delaySteps steps ago
        }
    }
}
