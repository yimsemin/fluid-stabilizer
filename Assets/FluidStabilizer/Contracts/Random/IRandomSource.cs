namespace FluidStabilizer.Contracts
{
    /// <summary>
    /// Deterministic pseudo-random source. Every stochastic element in the sim
    /// (sensor noise, domain randomization, driver profiles, initial conditions)
    /// draws from one of these. Given the same root seed the whole episode is
    /// reproducible. Never use <c>UnityEngine.Random</c> or an unseeded
    /// <c>System.Random</c> in the sim path.
    /// </summary>
    public interface IRandomSource
    {
        /// <summary>The seed this stream was created from (for logging / replay).</summary>
        ulong Seed { get; }

        /// <summary>Uniform in [0, 1).</summary>
        double NextDouble();

        /// <summary>Uniform in [min, max).</summary>
        double NextDouble(double min, double max);

        /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
        int NextInt(int minInclusive, int maxExclusive);

        /// <summary>Standard normal (mean 0, stddev 1), via Box–Muller.</summary>
        double NextGaussian();

        /// <summary>Normal with the given mean and standard deviation.</summary>
        double NextGaussian(double mean, double stddev);

        /// <summary>
        /// Derive a named independent sub-stream. The same name always yields the
        /// same sub-stream for a given parent, so streams can be added without
        /// perturbing existing ones.
        /// </summary>
        IRandomSource Fork(string name);
    }
}
