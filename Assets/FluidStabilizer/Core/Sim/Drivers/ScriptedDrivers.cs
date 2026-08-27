using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim.Drivers
{
    /// <summary>Holds fixed controls for the whole episode.</summary>
    public sealed class ConstantDriver : IDriverInput
    {
        private readonly DriverControls _controls;
        public ConstantDriver(DriverControls controls) { _controls = controls; }
        public string Name => "Constant";
        public void Reset() { }
        public DriverControls Sample(double simTimeSeconds) => _controls;
    }

    /// <summary>
    /// A deterministic scripted maneuver sequence for tests and smoke runs:
    /// cruise, hard brake, release, hard left, hard right, accelerate. Not a
    /// training driver — the driver-profile generator (design-review §17) comes
    /// later.
    /// </summary>
    public sealed class ScriptedManeuverDriver : IDriverInput
    {
        public string Name => "ScriptedManeuver";

        public void Reset() { }

        public DriverControls Sample(double t)
        {
            // piecewise schedule (seconds)
            if (t < 3.0) return Cruise();
            if (t < 5.0) return new DriverControls { Brake = 0.9 };
            if (t < 8.0) return Cruise();
            if (t < 10.0) return new DriverControls { Throttle = 0.3, Steer = 0.8 };
            if (t < 12.0) return Cruise();
            if (t < 14.0) return new DriverControls { Throttle = 0.3, Steer = -0.8 };
            if (t < 16.0) return Cruise();
            if (t < 19.0) return new DriverControls { Throttle = 1.0 };
            return Cruise();
        }

        private static DriverControls Cruise() => new DriverControls { Throttle = 0.12 };
    }
}
