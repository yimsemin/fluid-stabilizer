using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core
{
    /// <summary>
    /// <see cref="IClock"/> driven explicitly by the simulation loop: time only
    /// advances when <see cref="Advance"/> is called, always by exactly one fixed
    /// step. Time is accumulated as <c>StepCount * FixedStepSeconds</c> rather
    /// than by repeated addition, so it stays exact regardless of episode length.
    /// </summary>
    public sealed class ManualClock : IClock
    {
        public ManualClock(double fixedStepSeconds)
        {
            FixedStepSeconds = fixedStepSeconds;
            StepCount = 0;
        }

        public double FixedStepSeconds { get; }

        public long StepCount { get; private set; }

        public double SimTimeSeconds => StepCount * FixedStepSeconds;

        /// <summary>Advance the clock by one fixed step. Returns the new sim time.</summary>
        public double Advance()
        {
            StepCount++;
            return SimTimeSeconds;
        }

        /// <summary>Reset to the start of an episode.</summary>
        public void Reset()
        {
            StepCount = 0;
        }
    }
}
