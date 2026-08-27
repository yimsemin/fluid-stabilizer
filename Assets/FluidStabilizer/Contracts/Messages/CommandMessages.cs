using System;

namespace FluidStabilizer.Contracts
{
    /// <summary>How the actuator should interpret a <see cref="StabilizerCommand"/>.</summary>
    public enum StabilizerCommandMode
    {
        /// <summary>Per-axis commanded joint angular rate (rad/s). The RL action space.</summary>
        JointRate = 0,

        /// <summary>
        /// Per-axis target joint angle (rad); the actuator's position servo tracks
        /// it. Used by the analytic baselines, which are fundamentally "point the
        /// tank at the right tilt" (design-review §11 — angle-command option).
        /// </summary>
        JointAngle = 1
    }

    /// <summary>
    /// A stabilizer command from the controller/policy. The two axes are pitch
    /// and roll; <see cref="Mode"/> selects whether the values are rates or
    /// target angles.
    /// </summary>
    [Serializable]
    public struct StabilizerCommand
    {
        public long Sequence;
        public double FromStampSeconds;

        public StabilizerCommandMode Mode;

        /// <summary>rad/s when <see cref="Mode"/> is JointRate; rad when JointAngle.</summary>
        public double PitchCommand;
        public double RollCommand;

        public static StabilizerCommand Hold(long sequence, double fromStamp) => new StabilizerCommand
        {
            Sequence = sequence,
            FromStampSeconds = fromStamp,
            Mode = StabilizerCommandMode.JointRate,
            PitchCommand = 0.0,
            RollCommand = 0.0
        };

        public static StabilizerCommand Rate(long sequence, double fromStamp, double pitchRate, double rollRate)
            => new StabilizerCommand
            {
                Sequence = sequence, FromStampSeconds = fromStamp,
                Mode = StabilizerCommandMode.JointRate,
                PitchCommand = pitchRate, RollCommand = rollRate
            };

        public static StabilizerCommand Angle(long sequence, double fromStamp, double pitchAngle, double rollAngle)
            => new StabilizerCommand
            {
                Sequence = sequence, FromStampSeconds = fromStamp,
                Mode = StabilizerCommandMode.JointAngle,
                PitchCommand = pitchAngle, RollCommand = rollAngle
            };
    }
}
