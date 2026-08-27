using FluidStabilizer.Contracts;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// The null baseline: never moves the stabilizer. Represents "no active
    /// control" — the tank is held rigid relative to the truck bed. Every other
    /// controller and the RL policy are scored against this and against the
    /// analytic / PID baselines (design-review §2).
    /// </summary>
    public sealed class HoldController : IController
    {
        private long _sequence;

        public string Name => "Hold (no control)";

        public void Reset()
        {
            _sequence = 0;
        }

        public StabilizerCommand ComputeCommand(in SensorBundle sensors)
        {
            // Explicit angle-hold at zero (not a zero-rate command) so the tank
            // is held rigid to the truck bed even under external slosh load.
            return StabilizerCommand.Angle(_sequence++, sensors.StampSeconds, 0.0, 0.0);
        }
    }
}
