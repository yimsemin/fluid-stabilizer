namespace FluidStabilizer.Contracts
{
    /// <summary>
    /// A stabilizer controller: sensors in, command out. This is the seam the
    /// design keeps swappable — an analytic baseline, a PID, a privileged MPC, or
    /// a learned policy all implement this, and the same implementation must run
    /// unchanged against the sim now and a ROS 2 bridge later.
    ///
    /// Implementations MUST be pure C# with no <c>UnityEngine</c> reference and
    /// MUST work off <see cref="SensorBundle.StampSeconds"/>, never frame counts
    /// or wall-clock time. They receive only <see cref="SensorBundle"/> — never
    /// simulation ground truth.
    /// </summary>
    public interface IController
    {
        /// <summary>Human-readable name for logging and eval comparison.</summary>
        string Name { get; }

        /// <summary>Called once at the start of every episode, before the first command.</summary>
        void Reset();

        /// <summary>
        /// Compute the next command from the latest synchronized sensor readings.
        /// Called at the control rate (100 Hz in v1).
        /// </summary>
        StabilizerCommand ComputeCommand(in SensorBundle sensors);
    }
}
