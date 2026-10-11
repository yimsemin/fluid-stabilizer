using System;

namespace FluidStabilizer.Core
{
    /// <summary>First-order low-pass filter, exact discrete pole for a fixed sample period.</summary>
    public sealed class LowPass
    {
        private readonly double _cutoffHz;
        private double _y;
        private bool _primed;

        public LowPass(double cutoffHz) { _cutoffHz = Math.Max(1e-6, cutoffHz); }

        public double Value => _y;

        public void CopyStateFrom(LowPass o) { _y = o._y; _primed = o._primed; }

        public void Reset(double value = 0.0)
        {
            _y = value;
            _primed = false;
        }

        public double Step(double x, double dt)
        {
            if (!_primed) { _y = x; _primed = true; return _y; }
            double a = 1.0 - Math.Exp(-2.0 * Math.PI * _cutoffHz * dt);
            _y += a * (x - _y);
            return _y;
        }
    }
}
