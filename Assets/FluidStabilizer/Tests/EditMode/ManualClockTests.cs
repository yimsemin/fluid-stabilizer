using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    public class ManualClockTests
    {
        [Test]
        public void Time_AccumulatesExactly_OverLongEpisode()
        {
            var clock = new ManualClock(0.002);
            for (int i = 0; i < 20_000; i++) clock.Advance(); // 40 s at 500 Hz

            Assert.AreEqual(20_000, clock.StepCount);
            // Exact because time = StepCount * FixedStep, not repeated addition.
            Assert.AreEqual(40.0, clock.SimTimeSeconds, 1e-12);
        }

        [Test]
        public void Reset_ReturnsToZero()
        {
            var clock = new ManualClock(0.002);
            for (int i = 0; i < 123; i++) clock.Advance();
            clock.Reset();

            Assert.AreEqual(0, clock.StepCount);
            Assert.AreEqual(0.0, clock.SimTimeSeconds);
        }
    }

    public class HoldControllerTests
    {
        [Test]
        public void AlwaysCommandsZeroRate_WithIncreasingSequence()
        {
            var c = new HoldController();
            c.Reset();

            var s = new SensorBundle { StampSeconds = 1.0 };
            var first = c.ComputeCommand(s);
            var second = c.ComputeCommand(s);

            Assert.AreEqual(0.0, first.PitchCommand);
            Assert.AreEqual(0.0, first.RollCommand);
            Assert.AreEqual(0, first.Sequence);
            Assert.AreEqual(1, second.Sequence);
            Assert.AreEqual(1.0, first.FromStampSeconds);
        }
    }
}
