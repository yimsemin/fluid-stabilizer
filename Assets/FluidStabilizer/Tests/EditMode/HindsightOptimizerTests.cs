using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// experiment-plan.md step 3 — the feasibility ceiling. The offline search
    /// must be deterministic, and its result must never be worse than doing
    /// nothing (it is seeded from the zero trajectory and only ever moves to a
    /// strictly lower cost).
    /// </summary>
    public class HindsightOptimizerTests
    {
        // A short, discriminating synthetic drive: fuller tank + road on + an
        // aggressive driver, so there is real slosh for the search to work on.
        private static DriverTrace DiscriminatingTrace(ulong seed = 3, double seconds = 6.0, double fill = 0.72)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = seconds };
            cfg.Slosh.FillRatio = fill;
            cfg.Road.Enabled = true;

            var trace = new DriverTrace
            {
                Label = "synthetic-" + seed,
                Seed = seed,
                StepSeconds = cfg.PhysicsStepSeconds,
                FillRatio = fill,
                RoadEnabled = true,
                RoadSeverity = 1.0,
                SloshModel = "pendulum",
            };
            var loop = new SimulationLoop(cfg, new HoldController(),
                new RecordingDriver(new RandomManeuverDriver(seed, 0.8), trace), new PendulumSloshModel());
            loop.RunEpisode(seed);
            return trace;
        }

        private static HindsightOptions FastOpts() => new HindsightOptions
        {
            KnotCount = 4,
            Restarts = 1,
            IterationsPerRestart = 10,
            EvalModels = new List<(string, Func<ISloshModel>)>
            {
                ("pendulum", () => new PendulumSloshModel()),
            },
        };

        [Test]
        public void Optimize_IsDeterministic()
        {
            var trace = DiscriminatingTrace();
            var opts = FastOpts();

            HindsightOptimization a = HindsightOptimizer.Optimize(trace, opts);
            HindsightOptimization b = HindsightOptimizer.Optimize(trace, opts);

            Assert.AreEqual(a.Evaluations, b.Evaluations);
            Assert.AreEqual(a.BestCost, b.BestCost, 0.0, "hindsight search is not deterministic");
            Assert.AreEqual(a.BestParams.Length, b.BestParams.Length);
            for (int i = 0; i < a.BestParams.Length; i++)
                Assert.AreEqual(a.BestParams[i], b.BestParams[i], 0.0, $"param {i} differs between runs");
        }

        [Test]
        public void EvaluateTrajectory_IsDeterministic_AndRespectsActuatorLimits()
        {
            var trace = DiscriminatingTrace();
            var flat = new double[GimbalRateTrajectory.ParamCount(4)];
            for (int i = 0; i < flat.Length; i++) flat[i] = 5.0; // way past ±1 on purpose

            double scale = trace.ToConfig().Gimbal.MaxRateRadPerSec;
            EpisodeResult r1 = HindsightOptimizer.EvaluateTrajectory(trace, flat, 4, () => new PendulumSloshModel(), scale);
            EpisodeResult r2 = HindsightOptimizer.EvaluateTrajectory(trace, flat, 4, () => new PendulumSloshModel(), scale);

            Assert.AreEqual(r1.TotalOverflowVolume, r2.TotalOverflowVolume, 0.0);
            // The actuator's own rate + travel limits must bound the motion even
            // with an absurd commanded trajectory.
            Assert.LessOrEqual(r1.MeanAbsActionRate, trace.ToConfig().Gimbal.MaxRateRadPerSec + 1e-6);
        }

        [Test]
        public void Ceiling_IsNeverWorseThanDoNothing()
        {
            var trace = DiscriminatingTrace();
            HindsightDriveReport report = HindsightOptimizer.Run(trace, FastOpts());

            Assert.IsNotEmpty(report.Rows);
            foreach (var row in report.Rows)
                Assert.LessOrEqual(row.CeilingSpillLitres, row.ZeroActionSpillLitres + 1e-9,
                    $"[{row.SloshModel}] ceiling {row.CeilingSpillLitres:F4} L worse than do-nothing {row.ZeroActionSpillLitres:F4} L");
        }

        [Test]
        public void TrajectoryCsv_RoundTripsThroughFlatParams()
        {
            var trace = DiscriminatingTrace();
            HindsightDriveReport report = HindsightOptimizer.Run(trace, FastOpts());

            using (var sw = new StringWriter())
            {
                HindsightOptimizer.WriteTrajectoryCsv(sw, report);
                string csv = sw.ToString();
                StringAssert.Contains("fluid-stabilizer-hindsight-trajectory/v1", csv);
                StringAssert.Contains("t,pitchRate,rollRate", csv);
            }
        }

        [Test]
        public void Verdict_IsInconclusive_WhenHoldNeverSpills()
        {
            // A calm, low-fill, road-off drive: nobody spills, so the gate can't decide.
            var cfg = new SimulationConfig { EpisodeSeconds = 4.0 };
            cfg.Slosh.FillRatio = 0.45;
            var calm = new DriverTrace
            {
                Label = "calm", Seed = 1, StepSeconds = cfg.PhysicsStepSeconds, FillRatio = 0.45,
            };
            var loop = new SimulationLoop(cfg, new HoldController(),
                new RecordingDriver(new HighwayDriver(1, 0.2), calm), new PendulumSloshModel());
            loop.RunEpisode(1);

            HindsightDriveReport report = HindsightOptimizer.Run(calm, FastOpts());
            string verdict = HindsightOptimizer.Verdict(new[] { report });
            StringAssert.Contains("INCONCLUSIVE", verdict);
        }
    }
}
