using System;

namespace FluidStabilizer.Contracts
{
    /// <summary>
    /// A stabilizer command from the controller/policy. v1 action space is a
    /// per-axis commanded joint angular rate (rad/s); a low-level actuator
    /// controller in the Sim layer tracks it subject to torque/rate/travel
    /// limits, backlash, friction and transport delay.
    /// </summary>
    [Serializable]
    public struct StabilizerCommand
    {
        /// <summary>Monotonic sequence number, set by the controller.</summary>
        public long Sequence;

        /// <summary>The sensor-bundle stamp this command was computed from.</summary>
        public double FromStampSeconds;

        /// <summary>Commanded joint angular rate (rad/s).</summary>
        public double PitchRateCommand;
        public double RollRateCommand;

        public static StabilizerCommand Hold(long sequence, double fromStamp) => new StabilizerCommand
        {
            Sequence = sequence,
            FromStampSeconds = fromStamp,
            PitchRateCommand = 0.0,
            RollRateCommand = 0.0
        };
    }
}
