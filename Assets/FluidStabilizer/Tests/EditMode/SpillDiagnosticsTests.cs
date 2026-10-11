using NUnit.Framework;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Tests
{
    /// <summary>MPC step 0: the spill breakdown must account for every litre and be deterministic.</summary>
    public class SpillDiagnosticsTests
    {
        private static DriverTrace Rec(ulong seed)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 8.0 };
            cfg.Slosh.FillRatio = 0.72;
            var trace = new DriverTrace
            {
                Label = "diag-" + seed, Seed = seed, StepSeconds = cfg.PhysicsStepSeconds, FillRatio = 0.72
            };
            var loop = new SimulationLoop(cfg, new HoldController(),
                new RecordingDriver(new RandomManeuverDriver(seed, 1.0), trace), new PendulumSloshModel());
            loop.BeginEpisode(seed);
            while (loop.StepPhysics()) { }
            return trace;
        }

        [Test]
        public void BucketsSumToTotal_AndMatchRigidEvaluator()
        {
            DriverTrace t = Rec(11);
            SpillBreakdown b = SpillDiagnostics.Analyse(t, new HoldController(), _ => new PendulumSloshModel(), 4.0);

            Assert.AreEqual(b.TotalLitres, b.DuringLitres + b.ShortAfterLitres + b.LongAfterLitres, 1e-9);

            var loop = new SimulationLoop(t.ToConfig(), new HoldController(), new RecordedTraceDriver(t), new PendulumSloshModel());
            double expected = loop.RunEpisode(t.Seed).TotalOverflowVolume * 1000.0;
            Assert.AreEqual(expected, b.TotalLitres, 1e-9);
        }
    }
}
