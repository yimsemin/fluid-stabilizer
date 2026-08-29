using System.Linq;
using NUnit.Framework;
using UnityEngine;
using FluidStabilizer.Sim;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// experiment-plan.md step 2: replaying one recorded drive through several
    /// stabilizers at once must be deterministic and must agree, lane by lane,
    /// with a standalone run of the same controller.
    /// </summary>
    public class ComparisonRunnerTests
    {
        private static DriverTrace MakeTrace(ulong seed)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 10.0 };
            cfg.Slosh.FillRatio = 0.62;
            cfg.Road.Enabled = true;

            var trace = new DriverTrace
            {
                Label = "cmp", Seed = seed, StepSeconds = cfg.PhysicsStepSeconds,
                FillRatio = 0.62, RoadEnabled = true
            };
            var rec = new RecordingDriver(new RandomManeuverDriver(seed, 0.7), trace);
            var loop = new SimulationLoop(cfg, new HoldController(), rec, new PendulumSloshModel());
            loop.BeginEpisode(seed);
            while (loop.StepPhysics()) { }
            return trace;
        }

        [Test]
        public void EachLane_MatchesAStandaloneRunOfThatController_AndIsDeterministic()
        {
            var trace = MakeTrace(77);

            var go = new GameObject("cmp-test");
            try
            {
                var cmp = go.AddComponent<ComparisonRunner>();
                cmp.Lanes = new[]
                {
                    StabilizerChoice.Off,
                    StabilizerChoice.SpecificForceAlignmentPlusI,
                    StabilizerChoice.PrivilegedFeedforward,
                };

                cmp.StartComparison(trace, withRigs: false);
                var a = cmp.RunToEndHeadless();

                cmp.StartComparison(trace, withRigs: false);
                var b = cmp.RunToEndHeadless();

                foreach (var lane in cmp.Lanes)
                    Assert.AreEqual(a[lane], b[lane], 0.0, $"{lane}: comparison not deterministic");

                // Each lane == a standalone loop with the same controller / trace / seed.
                foreach (var lane in cmp.Lanes)
                {
                    double standalone = StandaloneSpillLitres(trace, lane);
                    Assert.AreEqual(standalone, a[lane], 0.0,
                        $"{lane}: comparison lane diverged from a standalone run");
                }

                // The lanes are genuinely different controllers, not copies.
                Assert.AreNotEqual(a[StabilizerChoice.Off], a[StabilizerChoice.SpecificForceAlignmentPlusI],
                    "rigid and PID produced identical spill — lanes are not independent");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static double StandaloneSpillLitres(DriverTrace trace, StabilizerChoice choice)
        {
            var loop = new SimulationLoop(
                trace.ToConfig(),
                SimulationRunner.MakeController(choice),
                new RecordedTraceDriver(trace),
                trace.MakeSloshModel());
            loop.BeginEpisode(trace.Seed);
            while (loop.StepPhysics()) { }
            return loop.LatestGroundTruth.CumulativeOverflowVolume * 1000.0;
        }
    }
}
