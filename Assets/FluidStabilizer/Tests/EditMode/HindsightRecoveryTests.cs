using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// F6 asks "can hindsight beat the rigid tank?" and answered no. But
    /// <see cref="HindsightOptimizer"/> is a finite-budget (1+1)-ES over a
    /// low-dimensional spline: a *search* failure and an *impossibility* are
    /// different claims. These tests build a single-axis case where an improving
    /// action provably exists **under the real actuator limits** (not the
    /// actuator-free ideal-tilt diagnostic) and check the searcher recovers it.
    /// If it cannot, F6 is a statement about the optimiser, not about control.
    /// </summary>
    public class HindsightRecoveryTests
    {
        // One long steady turn: sustained lateral specific force, roll axis only.
        // A constant tilt into the turn is reachable under a rate-limited gimbal
        // (ramp, then hold), so an improving trajectory exists by construction.
        private static DriverTrace SteadyTurn(double fill, double seconds, double steer)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = seconds };
            cfg.Slosh.FillRatio = fill;
            cfg.Road.Enabled = false;
            var trace = new DriverTrace { Label = $"steady-turn-{steer:0.00}" };
            trace.CaptureConfig(cfg, 4242UL, "pendulum", "Hold");
            var driver = new ConstantDriver(new DriverControls { Throttle = 0.35, Brake = 0.0, Steer = steer });
            var loop = new SimulationLoop(cfg, new HoldControllerLocal(),
                new RecordingDriver(driver, trace), new PendulumSloshModel());
            loop.RunEpisode(4242UL);
            return trace;
        }

        private sealed class HoldControllerLocal : IController
        {
            private long _s;
            public string Name => "Rigid (Hold, locked)";
            public void Reset() => _s = 0;
            public StabilizerCommand ComputeCommand(in SensorBundle sensors)
                => StabilizerCommand.Hold(_s++, sensors.StampSeconds);
        }

        private static double HoldSpill(DriverTrace t)
        {
            var loop = new SimulationLoop(t.ToConfig(), new HoldControllerLocal(),
                new RecordedTraceDriver(t), t.MakeSloshModel());
            return loop.RunEpisode(t.Seed).TotalOverflowVolume * 1000.0;
        }

        // Constant roll rate for the first `rampFraction` of the drive, then zero:
        // a plain "tilt in and stay there" that the actuator can actually execute.
        private static double[] TiltThenHold(int knots, double rollValue, double rampFraction)
        {
            var flat = new double[2 * knots];               // pitch stays zero
            int rampKnots = Math.Max(1, (int)Math.Round(knots * rampFraction));
            for (int i = 0; i < rampKnots; i++) flat[knots + i] = rollValue;
            return flat;
        }

        private static double Spill(DriverTrace t, double[] flat, int knots, double rateScale) =>
            HindsightOptimizer.EvaluateTrajectory(t, flat, knots, () => new PendulumSloshModel(), rateScale)
                .TotalOverflowVolume * 1000.0;

        /// <summary>
        /// The guard behind every "hindsight could not beat rigid" claim: when an
        /// improving action *does* exist under real actuator limits, the searcher
        /// at its normal budget must find it. If this test ever fails, F5/F6 stop
        /// being statements about control and become statements about the ES.
        /// </summary>
        [Test]
        public void Searcher_RecoversAKnownImprovement_UnderRealActuatorLimits()
        {
            var t = SteadyTurn(0.62, 14.0, 0.6);
            double hold = HoldSpill(t);
            double rateScale = t.ToConfig().Gimbal.MaxRateRadPerSec;
            double hand = Spill(t, TiltThenHold(8, -0.88, 0.3), 8, rateScale);

            Assert.Less(hand, hold * 0.5,
                "scenario is not a valid diagnostic — no hand-built improvement exists to recover");

            var opts = new HindsightOptions { KnotCount = 8, Restarts = 1, IterationsPerRestart = 40 };
            var o = HindsightOptimizer.Optimize(t, opts);
            double found = HindsightOptimizer.EvaluateTrajectory(
                t, o.BestParams, opts.KnotCount, () => new PendulumSloshModel(), o.RateScaleRadPerSec)
                .TotalOverflowVolume * 1000.0;

            // measured 2026-09-10: hold 1.951 → hand-built 0.187 → ES 0.166 (recovers ~101%)
            Assert.Less(found, hold * 0.5, "the searcher failed to recover an improvement that exists");
            Assert.Less(found, hand * 1.25, "the searcher landed far short of a plain tilt-and-hold");
        }

        [Test, Explicit("diagnostic — does the ES recover the known improvement?")]
        public void PrintSearcherRecovery()
        {
            foreach (double fill in new[] { 0.62, 0.70 })
            {
                var t = SteadyTurn(fill, 14.0, 0.6);
                double hold = HoldSpill(t);
                double rateScale = t.ToConfig().Gimbal.MaxRateRadPerSec;
                double hand = Spill(t, TiltThenHold(8, -0.88, 0.3), 8, rateScale);

                foreach (int knots in new[] { 4, 8, 16 })
                foreach (var budget in new[] { (r: 1, i: 40), (r: 3, i: 80), (r: 3, i: 200) })
                {
                    var opts = new HindsightOptions
                    {
                        KnotCount = knots, Restarts = budget.r, IterationsPerRestart = budget.i,
                    };
                    var o = HindsightOptimizer.Optimize(t, opts);
                    double found = HindsightOptimizer.EvaluateTrajectory(
                        t, o.BestParams, knots, () => new PendulumSloshModel(), o.RateScaleRadPerSec)
                        .TotalOverflowVolume * 1000.0;
                    TestContext.WriteLine(
                        $"fill={fill} knots={knots} restarts={budget.r} iters={budget.i} evals={o.Evaluations}  " +
                        $"hold={hold:F3}  handBuilt={hand:F3}  ES={found:F3}  " +
                        $"recovered={(hold - found) / (hold - hand):P0}");
                }
            }
        }

        [Test, Explicit("diagnostic — same recovery question under shallow water")]
        public void PrintSearcherRecovery_ShallowWater()
        {
            // Bracket discipline (F3): a failure under one slosh model is not a
            // failure under the other, so the shallow-water case gets its own run.
            // Budget is deliberately small — shallow water costs ~100x the pendulum
            // per episode, and a 243-evaluation search here runs for hours.
            const double fill = 0.70;
            var t = SteadyTurn(fill, 8.0, 0.6);
            var cfg = t.ToConfig();
            double rateScale = cfg.Gimbal.MaxRateRadPerSec;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var holdLoop = new SimulationLoop(cfg, new HoldControllerLocal(),
                new RecordedTraceDriver(t), new ShallowWaterSloshModel());
            double hold = holdLoop.RunEpisode(t.Seed).TotalOverflowVolume * 1000.0;
            double perEpisode = sw.Elapsed.TotalSeconds;
            TestContext.WriteLine($"[shallow] one episode = {perEpisode:F1} s  hold={hold:F3} L");

            double hand = HindsightOptimizer.EvaluateTrajectory(
                t, TiltThenHold(8, -0.88, 0.3), 8, () => new ShallowWaterSloshModel(), rateScale)
                .TotalOverflowVolume * 1000.0;
            TestContext.WriteLine($"[shallow] handBuilt={hand:F3} L  gain={hold - hand:F3} L");

            var opts = new HindsightOptions
            {
                KnotCount = 6, Restarts = 1, IterationsPerRestart = 25,
                OptimizeModel = () => new ShallowWaterSloshModel(),
                OptimizeModelName = "shallow",
                EvalModels = new System.Collections.Generic.List<(string, Func<ISloshModel>)>
                    { ("shallow", () => new ShallowWaterSloshModel()) },
            };
            sw.Restart();
            var o = HindsightOptimizer.Optimize(t, opts);
            double found = HindsightOptimizer.EvaluateTrajectory(
                t, o.BestParams, opts.KnotCount, () => new ShallowWaterSloshModel(), o.RateScaleRadPerSec)
                .TotalOverflowVolume * 1000.0;
            // measured 2026-09-10 (fill 0.70, 8 s): one episode 4.2 s, hold 16.986,
            // hand-built 6.014, ES 8.061 at 26 evals — 81% of a known improvement
            // recovered on the *expensive* model at a deliberately tiny budget.
            TestContext.WriteLine($"[shallow] ES={found:F3} L  evals={o.Evaluations}  " +
                                  $"search={sw.Elapsed.TotalMinutes:F1} min  " +
                                  $"recovered={(hold - found) / (hold - hand):P0}");
        }

        [Test, Explicit("diagnostic — prints the single-axis recovery numbers")]
        public void PrintSingleAxisRecoveryLandscape()
        {
            foreach (double fill in new[] { 0.62, 0.70 })
            foreach (double steer in new[] { 0.35, 0.6 })
            {
                var t = SteadyTurn(fill, 14.0, steer);
                double hold = HoldSpill(t);
                double rateScale = t.ToConfig().Gimbal.MaxRateRadPerSec;
                const int knots = 8;
                double bestHand = double.MaxValue, bestV = 0, bestF = 0;
                for (int i = -8; i <= 8; i++)
                foreach (double frac in new[] { 0.15, 0.3, 0.5 })
                {
                    double v = i / 8.0;
                    double s = Spill(t, TiltThenHold(knots, v, frac), knots, rateScale);
                    if (s < bestHand) { bestHand = s; bestV = v; bestF = frac; }
                }
                TestContext.WriteLine(
                    $"fill={fill} steer={steer}  hold={hold:F3} L  bestHandBuilt={bestHand:F3} L " +
                    $"(roll={bestV:F2}, ramp={bestF})  gain={hold - bestHand:F3} L");
            }
        }
    }
}
