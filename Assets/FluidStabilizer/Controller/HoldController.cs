using FluidStabilizer.Contracts;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// The null baseline: no active stabilization. The joints are mechanically
    /// <b>locked</b> at zero — the tank is rigid to the vehicle and the slosh
    /// moment is reacted by the vehicle structure, not the actuator, so it cannot
    /// saturate or sag (decision D5). This is the true performance floor every
    /// other controller and the RL policy are scored against (design-review §2).
    /// </summary>
    public sealed class HoldController : IController
    {
        private long _sequence;

        public string Name => "Hold (locked, no control)";

        public void Reset()
        {
            _sequence = 0;
        }

        public StabilizerCommand ComputeCommand(in SensorBundle sensors)
            => StabilizerCommand.Hold(_sequence++, sensors.StampSeconds);
    }
}
