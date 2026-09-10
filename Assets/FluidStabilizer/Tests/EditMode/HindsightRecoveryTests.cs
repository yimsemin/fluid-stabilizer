using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Rl;

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
        private static DriverTrace SteadyTurn(double fill, double seconds, double steer, ulong seed = 4242UL)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = seconds };
            cfg.Slosh.FillRatio = fill;
            cfg.Road.Enabled = false;
            var trace = new DriverTrace { Label = $"steady-turn-{steer:0.00}" };
            trace.CaptureConfig(cfg, seed, "pendulum", "Hold");
            var driver = new ConstantDriver(new DriverControls { Throttle = 0.35, Brake = 0.0, Steer = steer });
            var loop = new SimulationLoop(cfg, new HoldControllerLocal(),
                new RecordingDriver(driver, trace), new PendulumSloshModel());
            loop.RunEpisode(seed);
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

        // Does the F7 steady-turn win survive a longer drive? The driving-premise
        // sweep saw it vanish at 25 s, which would make it a short-drive artifact
        // rather than an operating point worth demonstrating.
        // The driving-premise sweep found NO win in the same steady-turn regime the
        // F7 diagnostic won 91% in. Isolate which knob explains it: the recording
        // seed (the D7 slip proxy is seeded), the steer magnitude, or the budget.
        /// <summary>
        /// The demo condition (findings F8): a sustained corner on a smooth road is
        /// the one operating point found so far where the feasibility ceiling beats
        /// the rigid tank under BOTH slosh models. docs/demo-guide.md and
        /// DemoDriveRunner both rest on this; if it stops being true, they are wrong.
        /// </summary>
        [Test]
        public void DemoCondition_CeilingBeatsRigid_UnderBothSloshModels()
        {
            var t = SteadyTurn(0.62, 25.0, 0.45, 9000UL);

            var opts = new HindsightOptions
            {
                KnotCount = 8, Restarts = 3, IterationsPerRestart = 80, LockCapable = true,
                EvalModels = new System.Collections.Generic.List<(string, Func<ISloshModel>)>
                {
                    ("pendulum", () => new PendulumSloshModel()),
                    ("shallow", () => new ShallowWaterSloshModel()),
                },
            };
            var report = HindsightOptimizer.Run(t, opts);

            // measured 2026-09-10: pendulum 7.91 → 5.10 L, shallow 24.90 → 7.87 L
            Assert.AreEqual(2, report.Rows.Count, "expected both slosh models");
            foreach (var row in report.Rows)
            {
                double gain = row.HoldSpillLitres - row.CeilingSpillLitres;
                Assert.Greater(gain, 0.5,
                    $"{row.SloshModel}: ceiling {row.CeilingSpillLitres:F2} L no longer beats " +
                    $"Hold {row.HoldSpillLitres:F2} L by the practical margin");
                Assert.Greater(gain / row.HoldSpillLitres, 0.25,
                    $"{row.SloshModel}: ceiling gain fell below the 25% bar");
            }
        }

        /// <summary>
        /// The fix F9 asks for: with an explicit lock action the policy's floor is
        /// the rigid tank <b>whatever its rate residual is</b> — because it chooses
        /// the lock instead of having it inferred from that residual. That turns the
        /// cliff into a real floor: ③ ⊇ ① holds for any action, not only a = 0.
        /// </summary>
        [Test]
        public void ExplicitLock_KeepsTheRigidFloor_ForAnyResidual()
        {
            var t = SteadyTurn(0.62, 25.0, 0.45, 9000UL);
            SimulationConfig cfg = t.ToConfig();
            double rigid = HoldSpill(t);

            double SpillWithLockRequest(double residual, double lockRequest)
            {
                var composer = new ActionComposer(
                    0.08 * cfg.Gimbal.MaxRateRadPerSec, cfg.Gimbal.MaxRateRadPerSec,
                    new HoldControllerLocal(), new LockGate(cfg.Gimbal), explicitLock: true);
                var loop = new SimulationLoop(cfg,
                    new ConstantResidual(composer, residual, lockRequest),
                    new RecordedTraceDriver(t), new PendulumSloshModel());
                return loop.RunEpisode(t.Seed).TotalOverflowVolume * 1000.0;
            }

            // Always ask for the lock: exactly the rigid tank, at every residual —
            // including the ones that fell off the dead-band cliff at ~19 L.
            foreach (double residual in new[] { 0.0, 0.3, 1.0, -1.0 })
                Assert.AreEqual(rigid, SpillWithLockRequest(residual, 1.0), 1e-9,
                    $"explicit lock must hold the rigid floor at residual {residual}");

            // Never ask for it: the servo runs and the outcome is genuinely worse,
            // so the two choices are distinguishable — there is something to learn.
            Assert.Greater(SpillWithLockRequest(1.0, -1.0), rigid * 2.0);
        }

        /// <summary>Constant residual + constant lock request, set every control tick.</summary>
        private sealed class ConstantResidual : IController
        {
            private readonly ActionComposer _c;
            private readonly double _a, _lock;
            public ConstantResidual(ActionComposer c, double a, double lockRequest)
            { _c = c; _a = a; _lock = lockRequest; }
            public string Name => "constant";
            public void Reset() => _c.Reset();
            public StabilizerCommand ComputeCommand(in SensorBundle s)
            {
                _c.SetAction(_a, _a, _lock);
                return _c.ComputeCommand(s);
            }
        }

        // Maps the cliff: amplitude, and whether frequency matters at all.
        [Test, Explicit("diagnostic — does a wobbling residual break the lock floor?")]
        public void PrintResidualFloor_VsActionWobble()
        {
            var t = SteadyTurn(0.62, 25.0, 0.45, 9000UL);
            SimulationConfig cfg = t.ToConfig();
            TestContext.WriteLine($"rigid (mechanically locked) = {HoldSpill(t):F3} L");

            foreach (double hz in new[] { 0.0, 0.2, 1.0, 5.0, 20.0 })
            foreach (double amp in new[] { 0.05, 0.3, 1.0 })
            {
                var composer = new ActionComposer(
                    0.08 * cfg.Gimbal.MaxRateRadPerSec, cfg.Gimbal.MaxRateRadPerSec,
                    new HoldControllerLocal(), new LockGate(cfg.Gimbal));
                var driver = new RecordedTraceDriver(t);
                var loop = new SimulationLoop(cfg, new WobblingResidual(composer, amp, hz),
                    driver, new PendulumSloshModel());
                var r = loop.RunEpisode(t.Seed);
                TestContext.WriteLine(
                    $"  {hz,5:F1} Hz  amp={amp,4:F2} → spill {r.TotalOverflowVolume * 1000.0,8:F3} L  " +
                    $"effort {r.MeanAbsActionRate:F3}  torqueSat {r.TorqueSaturationFraction:P0}");
            }
        }

        /// <summary>Feeds a sinusoidal residual into a composer, standing in for a
        /// policy whose output is not constant.</summary>
        private sealed class WobblingResidual : IController
        {
            private readonly ActionComposer _c;
            private readonly double _amp, _hz;
            public WobblingResidual(ActionComposer c, double amp, double hz) { _c = c; _amp = amp; _hz = hz; }
            public string Name => "wobble";
            public void Reset() => _c.Reset();
            public StabilizerCommand ComputeCommand(in SensorBundle s)
            {
                double a = _hz <= 0 ? _amp : _amp * Math.Sin(2.0 * Math.PI * _hz * s.StampSeconds);
                _c.SetAction(a, a);
                return _c.ComputeCommand(s);
            }
        }

        [Test, Explicit("diagnostic — why does the sweep disagree with F7?")]
        public void PrintSteadyTurnWin_AcrossSeedsAndSteer()
        {
            TestContext.WriteLine("seed  steer   hold[L]  servo@0[L]   ES(8k)[L]   ES(6k)[L]   gain(8k)");
            foreach (ulong seed in new ulong[] { 4242, 9000, 9001, 9002, 9003 })
            foreach (double steer in new[] { 0.45, 0.6, 0.75 })
            {
                var t = SteadyTurn(0.62, 25.0, steer, seed);
                double hold = HoldSpill(t);
                double rateScale = t.ToConfig().Gimbal.MaxRateRadPerSec;
                double zero = Spill(t, new double[16], 8, rateScale);
                double es8 = EsSpill(t, 8, 2, 50);
                double es6 = EsSpill(t, 6, 2, 50);
                TestContext.WriteLine(
                    $"{seed,5} {steer,6:F2} {hold,9:F3} {zero,11:F3} {es8,11:F3} {es6,11:F3} " +
                    $"{(hold > 1e-9 ? (hold - es8) / hold : 0),10:P0}");
            }
        }

        private static double EsSpill(DriverTrace t, int knots, int restarts, int iters)
        {
            var o = HindsightOptimizer.Optimize(t, new HindsightOptions
                { KnotCount = knots, Restarts = restarts, IterationsPerRestart = iters });
            return HindsightOptimizer.EvaluateTrajectory(
                t, o.BestParams, knots, () => new PendulumSloshModel(), o.RateScaleRadPerSec)
                .TotalOverflowVolume * 1000.0;
        }

        [Test, Explicit("diagnostic — is the steady-turn win duration-limited?")]
        public void PrintSteadyTurnWinVsDuration()
        {
            foreach (double seconds in new[] { 8.0, 14.0, 20.0, 25.0, 35.0 })
            {
                var t = SteadyTurn(0.62, seconds, 0.6);
                double hold = HoldSpill(t);
                double rateScale = t.ToConfig().Gimbal.MaxRateRadPerSec;
                double zero = Spill(t, new double[16], 8, rateScale);   // sagging servo
                double bestHand = double.MaxValue; double bestV = 0, bestF = 0;
                for (int i = -8; i <= 8; i++)
                foreach (double frac in new[] { 0.15, 0.3, 0.5 })
                {
                    double v = i / 8.0;
                    double sp = Spill(t, TiltThenHold(8, v, frac), 8, rateScale);
                    if (sp < bestHand) { bestHand = sp; bestV = v; bestF = frac; }
                }
                var o = HindsightOptimizer.Optimize(t,
                    new HindsightOptions { KnotCount = 8, Restarts = 2, IterationsPerRestart = 50 });
                double es = HindsightOptimizer.EvaluateTrajectory(
                    t, o.BestParams, 8, () => new PendulumSloshModel(), o.RateScaleRadPerSec)
                    .TotalOverflowVolume * 1000.0;
                TestContext.WriteLine(
                    $"{seconds,5:F0} s  hold={hold,8:F3}  servo@0={zero,8:F3}  hand={bestHand,8:F3} " +
                    $"(roll={bestV:F2},ramp={bestF})  ES={es,8:F3}  gain vs hold={hold - es,7:F3} L " +
                    $"({(hold > 1e-9 ? (hold - es) / hold : 0):P0})");
            }
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
