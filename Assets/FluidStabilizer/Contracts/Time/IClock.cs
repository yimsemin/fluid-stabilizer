namespace FluidStabilizer.Contracts
{
    /// <summary>
    /// The single source of simulation time. Everything physical is scheduled off
    /// this, never off Unity frame timing or wall-clock time. A future real-time /
    /// ROS 2 adapter provides a different implementation backed by wall time.
    /// </summary>
    public interface IClock
    {
        /// <summary>Seconds since the current episode started. Monotonic, starts at 0.</summary>
        double SimTimeSeconds { get; }

        /// <summary>The fixed physics integration step, seconds (2 ms in v1).</summary>
        double FixedStepSeconds { get; }

        /// <summary>Number of fixed steps executed since the episode started.</summary>
        long StepCount { get; }
    }
}
