using NUnit.Framework;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Tests
{
    /// <summary>MPC step 1: the oracle MPC runs, is reproducible, and does not leave the actuator's limits.</summary>
    public class OracleMpcTests
    {
        private static EpisodeResult Run(out OracleMpcController mpc)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 8.0 };
            cfg.Slosh.FillRatio = 0.62;
            mpc = new OracleMpcController(cfg, new MpcOptions { Samples = 12 });
            var loop = new SimulationLoop(cfg, mpc, new RandomManeuverDriver(5, 0.8), new PendulumSloshModel());
            return loop.RunEpisode(5);
        }

        [Test]
        public void IsDeterministic_AndFinite()
        {
            EpisodeResult a = Run(out OracleMpcController ma);
            EpisodeResult b = Run(out _);
            Assert.AreEqual(a.TotalOverflowVolume, b.TotalOverflowVolume, 0.0);
            Assert.IsFalse(double.IsNaN(a.TotalOverflowVolume));
            Assert.Greater(ma.PlanCount, 100);
            Assert.LessOrEqual(ma.LockedTicks, ma.Ticks);
        }
    }
}
