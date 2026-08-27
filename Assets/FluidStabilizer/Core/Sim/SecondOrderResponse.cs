using System;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>
    /// A unit-DC-gain second-order system  ẍ + 2ζω ẋ + ω² x = ω² u  tracking a
    /// target <c>u</c>. Integrated semi-implicitly (symplectic Euler), which is
    /// stable for the step sizes used here and fully deterministic.
    ///
    /// Reused for the vehicle suspension response and the actuator rate loop.
    /// </summary>
    public sealed class SecondOrderResponse
    {
        private double _omega;
        private double _zeta;

        public double Value { get; private set; }
        public double Velocity { get; private set; }

        public SecondOrderResponse(double naturalFrequencyHz, double dampingRatio, double initialValue = 0.0)
        {
            Configure(naturalFrequencyHz, dampingRatio);
            Reset(initialValue);
        }

        public void Configure(double naturalFrequencyHz, double dampingRatio)
        {
            _omega = 2.0 * Math.PI * Math.Max(1e-6, naturalFrequencyHz);
            _zeta = Math.Max(0.0, dampingRatio);
        }

        public void Reset(double value = 0.0, double velocity = 0.0)
        {
            Value = value;
            Velocity = velocity;
        }

        /// <summary>Advance by <paramref name="dt"/> seconds toward <paramref name="target"/>.</summary>
        public void Step(double target, double dt)
        {
            double accel = _omega * _omega * (target - Value) - 2.0 * _zeta * _omega * Velocity;
            Velocity += accel * dt;
            Value += Velocity * dt;
        }

        /// <summary>The instantaneous acceleration (2nd derivative) for the current state and target.</summary>
        public double AccelerationToward(double target)
            => _omega * _omega * (target - Value) - 2.0 * _zeta * _omega * Velocity;
    }
}
