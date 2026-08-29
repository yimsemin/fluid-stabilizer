using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Core.Sim.Eval
{
    /// <summary>
    /// A low-dimensional gimbal-rate trajectory: per axis, <c>KnotCount</c>
    /// normalized ([-1, 1]-ish) knot values spread evenly over the drive, read
    /// back with a Catmull-Rom spline. The injected controller scales the spline
    /// value by the actuator's max rate. Overshoot past ±1 is fine — the actuator
    /// clamps the rate and the travel limit anyway.
    /// </summary>
    public sealed class GimbalRateTrajectory
    {
        public readonly double[] PitchKnots;
        public readonly double[] RollKnots;
        public readonly double DurationSeconds;

        public int KnotCount => PitchKnots.Length;

        public GimbalRateTrajectory(double[] pitchKnots, double[] rollKnots, double durationSeconds)
        {
            if (pitchKnots == null || rollKnots == null) throw new ArgumentNullException();
            if (pitchKnots.Length != rollKnots.Length || pitchKnots.Length < 1)
                throw new ArgumentException("pitch/roll knot arrays must be the same non-zero length");
            PitchKnots = pitchKnots;
            RollKnots = rollKnots;
            DurationSeconds = durationSeconds;
        }

        /// <summary>Normalized pitch/roll rate at drive time <paramref name="t"/> (s).</summary>
        public void Evaluate(double t, out double pitchNorm, out double rollNorm)
        {
            double u = DurationSeconds > 0.0 ? t / DurationSeconds : 0.0;
            pitchNorm = CatmullRom(PitchKnots, u);
            rollNorm = CatmullRom(RollKnots, u);
        }

        public static int ParamCount(int knotCount) => 2 * knotCount;

        /// <summary>[pitch knots…, roll knots…] as one flat vector.</summary>
        public double[] ToFlat()
        {
            var f = new double[2 * KnotCount];
            Array.Copy(PitchKnots, 0, f, 0, KnotCount);
            Array.Copy(RollKnots, 0, f, KnotCount, KnotCount);
            return f;
        }

        public static GimbalRateTrajectory FromFlat(double[] flat, int knotCount, double durationSeconds)
        {
            if (flat.Length != 2 * knotCount) throw new ArgumentException("flat length != 2 * knotCount");
            var p = new double[knotCount];
            var r = new double[knotCount];
            Array.Copy(flat, 0, p, 0, knotCount);
            Array.Copy(flat, knotCount, r, 0, knotCount);
            return new GimbalRateTrajectory(p, r, durationSeconds);
        }

        private static double CatmullRom(double[] k, double u01)
        {
            int n = k.Length;
            if (n == 1) return k[0];
            double x = (u01 < 0.0 ? 0.0 : (u01 > 1.0 ? 1.0 : u01)) * (n - 1);
            int i = (int)Math.Floor(x);
            if (i > n - 2) i = n - 2;
            if (i < 0) i = 0;
            double f = x - i;
            double p0 = k[Math.Max(0, i - 1)];
            double p1 = k[i];
            double p2 = k[i + 1];
            double p3 = k[Math.Min(n - 1, i + 2)];
            double f2 = f * f, f3 = f2 * f;
            return 0.5 * (2.0 * p1
                          + (-p0 + p2) * f
                          + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * f2
                          + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * f3);
        }
    }

    /// <summary>
    /// Plays a fixed <see cref="GimbalRateTrajectory"/> into the loop as a
    /// per-axis joint-rate command — pure feed-forward, no sensor feedback. This
    /// is the "hindsight" actor: it knows the whole drive in advance and the
    /// offline optimiser has already shaped its trajectory. Same action space as
    /// the RL policy (<see cref="StabilizerCommandMode.JointRate"/>).
    /// </summary>
    public sealed class HindsightTrajectoryController : IController
    {
        private readonly GimbalRateTrajectory _traj;
        private readonly double _rateScaleRadPerSec;
        private long _seq;

        public HindsightTrajectoryController(GimbalRateTrajectory traj, double rateScaleRadPerSec)
        {
            _traj = traj ?? throw new ArgumentNullException(nameof(traj));
            _rateScaleRadPerSec = rateScaleRadPerSec;
        }

        public string Name => "Hindsight (offline-optimal trajectory)";

        public void Reset() => _seq = 0;

        public StabilizerCommand ComputeCommand(in SensorBundle sensors)
        {
            _traj.Evaluate(sensors.StampSeconds, out double p, out double r);
            return StabilizerCommand.Rate(_seq++, sensors.StampSeconds,
                Clamp1(p) * _rateScaleRadPerSec, Clamp1(r) * _rateScaleRadPerSec);
        }

        private static double Clamp1(double v) => v < -1.0 ? -1.0 : (v > 1.0 ? 1.0 : v);
    }

    /// <summary>The locked-joint "Hold" floor, local to Core (mirrors <c>HoldController</c>).</summary>
    internal sealed class LockedHoldController : IController
    {
        private long _seq;
        public string Name => "Rigid (Hold, locked)";
        public void Reset() => _seq = 0;
        public StabilizerCommand ComputeCommand(in SensorBundle sensors)
            => StabilizerCommand.Hold(_seq++, sensors.StampSeconds);
    }

    public sealed class HindsightOptions
    {
        /// <summary>Knots per axis. 8 → a ~2 s control-point spacing on a 15 s drive.</summary>
        public int KnotCount = 8;

        /// <summary>Independent (1+1)-ES searches. Restart 0 starts from the zero
        /// (do-nothing) trajectory, so the result can never beat-by-losing.</summary>
        public int Restarts = 3;

        public int IterationsPerRestart = 80;

        public double InitialSigma = 0.35;
        public double MinSigma = 2e-3;
        public double MaxSigma = 0.6;

        /// <summary>(1+1)-ES step-size adaptation (Rechenberg 1/5-rule flavour).</summary>
        public double SuccessExpand = 1.3;
        public double FailShrink = 0.85;

        /// <summary>Tiny tie-breaker so, spill being equal, the optimiser prefers
        /// the calmer trajectory. Litres of "cost" per rad/s of mean joint rate.</summary>
        public double EffortCostWeightLitresPerRadPerSec = 0.05;

        /// <summary>Fraction of <see cref="GimbalConfig.MaxRateRadPerSec"/> a knot
        /// value of ±1 maps to. 1.0 = full authority.</summary>
        public double RateAuthority = 1.0;

        public ulong OptimizerSeed = 0xB16B00B5UL;

        /// <summary>The slosh model the search optimises against. Bracket
        /// discipline (F3): optimise on the cheap pendulum ROM, then score the
        /// found trajectory under every model in <see cref="EvalModels"/>.</summary>
        public Func<ISloshModel> OptimizeModel = () => new PendulumSloshModel();
        public string OptimizeModelName = "pendulum";

        public List<(string name, Func<ISloshModel> make)> EvalModels =
            new List<(string, Func<ISloshModel>)>
            {
                ("pendulum", () => new PendulumSloshModel()),
                ("shallow", () => new ShallowWaterSloshModel()),
            };
    }

    public struct HindsightOptimization
    {
        public double[] BestParams;        // flat, 2 * KnotCount
        public int KnotCount;
        public double DurationSeconds;
        public double RateScaleRadPerSec;
        public int Evaluations;
        public double BestCost;            // litres + effort tie-breaker, on OptimizeModel
    }

    public struct HindsightModelRow
    {
        public string SloshModel;
        public double HoldSpillLitres;
        public double ZeroActionSpillLitres;
        public double CeilingSpillLitres;
        public double CeilingWorstFreeboard;    // m (negative = overflowed)
        public double CeilingEffort;            // mean |joint rate|, rad/s
        public double CeilingTorqueSatFraction;
        public double CeilingAngleLimitFraction;
    }

    public sealed class HindsightDriveReport
    {
        public string Trace;
        public double DurationSeconds;
        public double FillRatio;
        public bool RoadEnabled;
        public string OptimizeModel;
        public int KnotCount;
        public int Evaluations;
        public double[] BestParams;
        public double RateScaleRadPerSec;
        public readonly List<HindsightModelRow> Rows = new List<HindsightModelRow>();
    }

    /// <summary>
    /// experiment-plan.md step 3 — the <b>feasibility ceiling</b>. For one fixed
    /// recorded drive, search offline (gradient-free, full hindsight, real
    /// actuator limits) for the gimbal-rate trajectory that minimises spill, then
    /// compare it to the rigid locked "Hold" floor. This is the decision gate:
    /// if hindsight cannot beat Hold, no causal policy will, and the premise has
    /// to change before spending effort on RL (docs/rl.md "First-run result").
    ///
    /// Deliberately simple — a couple of (1+1)-ES restarts over a spline, not a
    /// real-time MPC. It answers the same yes/no question the design review asks
    /// for and nothing more.
    /// </summary>
    public static class HindsightOptimizer
    {
        /// <summary>Replay <paramref name="trace"/> with the given flat trajectory params under one slosh model.</summary>
        public static EpisodeResult EvaluateTrajectory(
            DriverTrace trace, double[] flatParams, int knotCount,
            Func<ISloshModel> sloshModel, double rateScaleRadPerSec)
        {
            var traj = GimbalRateTrajectory.FromFlat(flatParams, knotCount, trace.DurationSeconds);
            var loop = new SimulationLoop(
                trace.ToConfig(),
                new HindsightTrajectoryController(traj, rateScaleRadPerSec),
                new RecordedTraceDriver(trace),
                sloshModel());
            return loop.RunEpisode(trace.Seed);
        }

        /// <summary>Run the offline search for one drive. Deterministic given the trace and options.</summary>
        public static HindsightOptimization Optimize(DriverTrace trace, HindsightOptions opts)
        {
            if (opts.KnotCount < 1) throw new ArgumentException("KnotCount must be >= 1");

            double duration = trace.DurationSeconds;
            double rateScale = trace.ToConfig().Gimbal.MaxRateRadPerSec * opts.RateAuthority;
            int d = GimbalRateTrajectory.ParamCount(opts.KnotCount);
            int evals = 0;

            double Cost(double[] p)
            {
                EpisodeResult r = EvaluateTrajectory(trace, p, opts.KnotCount, opts.OptimizeModel, rateScale);
                evals++;
                return r.TotalOverflowVolume * 1000.0
                       + opts.EffortCostWeightLitresPerRadPerSec * r.MeanAbsActionRate;
            }

            var rng = new Pcg32(Pcg32.SplitMix64(opts.OptimizerSeed ^ trace.Seed
                                                 ^ Pcg32.Fnv1a64(opts.OptimizeModelName ?? "")));

            var best = new double[d];               // zero (do-nothing) trajectory
            double bestCost = Cost(best);

            for (int restart = 0; restart < Math.Max(1, opts.Restarts); restart++)
            {
                double[] x;
                double xCost;
                if (restart == 0)
                {
                    x = (double[])best.Clone();
                    xCost = bestCost;
                }
                else
                {
                    x = new double[d];
                    for (int j = 0; j < d; j++) x[j] = -0.6 + 1.2 * rng.NextDouble();
                    xCost = Cost(x);
                    if (xCost < bestCost) { best = (double[])x.Clone(); bestCost = xCost; }
                }

                double sigma = opts.InitialSigma;
                for (int it = 0; it < opts.IterationsPerRestart; it++)
                {
                    var cand = new double[d];
                    for (int j = 0; j < d; j++)
                        cand[j] = Clamp(x[j] + sigma * Gaussian(rng), -1.0, 1.0);

                    double cCost = Cost(cand);
                    if (cCost + 1e-12 < xCost)
                    {
                        x = cand;
                        xCost = cCost;
                        sigma = Math.Min(opts.MaxSigma, sigma * opts.SuccessExpand);
                        if (xCost < bestCost) { best = (double[])x.Clone(); bestCost = xCost; }
                    }
                    else
                    {
                        sigma = Math.Max(opts.MinSigma, sigma * opts.FailShrink);
                    }
                }
            }

            return new HindsightOptimization
            {
                BestParams = best,
                KnotCount = opts.KnotCount,
                DurationSeconds = duration,
                RateScaleRadPerSec = rateScale,
                Evaluations = evals,
                BestCost = bestCost,
            };
        }

        /// <summary>Optimise one drive, then score the ceiling vs the Hold floor vs do-nothing under every eval model.</summary>
        public static HindsightDriveReport Run(DriverTrace trace, HindsightOptions opts)
        {
            HindsightOptimization opt = Optimize(trace, opts);

            var report = new HindsightDriveReport
            {
                Trace = trace.Label,
                DurationSeconds = trace.DurationSeconds,
                FillRatio = trace.FillRatio,
                RoadEnabled = trace.RoadEnabled,
                OptimizeModel = opts.OptimizeModelName,
                KnotCount = opt.KnotCount,
                Evaluations = opt.Evaluations,
                BestParams = opt.BestParams,
                RateScaleRadPerSec = opt.RateScaleRadPerSec,
            };

            var zero = new double[GimbalRateTrajectory.ParamCount(opt.KnotCount)];

            foreach (var (name, make) in opts.EvalModels)
            {
                var holdLoop = new SimulationLoop(
                    trace.ToConfig(), new LockedHoldController(), new RecordedTraceDriver(trace), make());
                EpisodeResult hold = holdLoop.RunEpisode(trace.Seed);

                EpisodeResult none = EvaluateTrajectory(trace, zero, opt.KnotCount, make, opt.RateScaleRadPerSec);
                EpisodeResult ceil = EvaluateTrajectory(trace, opt.BestParams, opt.KnotCount, make, opt.RateScaleRadPerSec);

                report.Rows.Add(new HindsightModelRow
                {
                    SloshModel = name,
                    HoldSpillLitres = hold.TotalOverflowVolume * 1000.0,
                    ZeroActionSpillLitres = none.TotalOverflowVolume * 1000.0,
                    CeilingSpillLitres = ceil.TotalOverflowVolume * 1000.0,
                    CeilingWorstFreeboard = ceil.WorstFreeboard,
                    CeilingEffort = ceil.MeanAbsActionRate,
                    CeilingTorqueSatFraction = ceil.TorqueSaturationFraction,
                    CeilingAngleLimitFraction = ceil.AngleLimitFraction,
                });
            }

            return report;
        }

        // ---------------------------------------------------------------- verdict

        /// <summary>
        /// The decision-gate call. "Meaningfully beats" = the ceiling spills at
        /// least <paramref name="minAbsoluteLitres"/> less than Hold <em>and</em>
        /// cuts it by at least <paramref name="minRelativeFraction"/>, under every
        /// slosh model — and holds up on drives where Hold actually spills.
        /// </summary>
        public static string Verdict(
            IReadOnlyList<HindsightDriveReport> reports,
            double minAbsoluteLitres = 0.5,
            double minRelativeFraction = 0.25)
        {
            var sb = new StringBuilder();
            var models = reports.SelectMany(r => r.Rows.Select(x => x.SloshModel)).Distinct().ToList();

            int discriminating = 0, beatsAllModels = 0, ceilingWorseAnyModel = 0;

            foreach (var r in reports)
            {
                bool anyModelSpillsUnderHold = r.Rows.Any(x => x.HoldSpillLitres > minAbsoluteLitres);
                if (!anyModelSpillsUnderHold) continue;
                discriminating++;

                bool beatsEveryModel = true;
                foreach (var row in r.Rows)
                {
                    double abs = row.HoldSpillLitres - row.CeilingSpillLitres;
                    double rel = row.HoldSpillLitres > 1e-9 ? abs / row.HoldSpillLitres : 0.0;
                    bool beats = abs >= minAbsoluteLitres && rel >= minRelativeFraction;
                    if (row.HoldSpillLitres <= minAbsoluteLitres) beats = true; // model where Hold is already dry: don't hold it against the ceiling
                    if (!beats) beatsEveryModel = false;
                    if (row.CeilingSpillLitres > row.HoldSpillLitres + minAbsoluteLitres) ceilingWorseAnyModel++;
                }
                if (beatsEveryModel) beatsAllModels++;
            }

            sb.AppendLine("### decision gate");
            sb.AppendLine($"  slosh models scored          : {string.Join(", ", models)}");
            sb.AppendLine($"  drives where Hold spills > {minAbsoluteLitres:F1} L : {discriminating}");
            sb.AppendLine($"  …of those, ceiling beats Hold under ALL models (≥{minAbsoluteLitres:F1} L and ≥{minRelativeFraction:P0}) : {beatsAllModels}");
            sb.AppendLine($"  (ceiling clearly worse than Hold on some model/drive) : {ceilingWorseAnyModel}");
            sb.AppendLine();

            if (discriminating == 0)
            {
                sb.AppendLine("  VERDICT: INCONCLUSIVE — no recorded drive makes the rigid Hold spill.");
                sb.AppendLine("  Record discriminating drives (road on, fill >= 0.60, a hard brake or");
                sb.AppendLine("  swerve) or raise fill / road severity, then re-run.");
            }
            else if (beatsAllModels >= Math.Max(1, (discriminating + 1) / 2) && ceilingWorseAnyModel == 0)
            {
                sb.AppendLine("  VERDICT: PASS — hindsight meaningfully beats the rigid Hold on the");
                sb.AppendLine("  discriminating drives, under both slosh models. The task is in the");
                sb.AppendLine("  winnable bracket: proceed to experiment-plan step 4 (residual RL),");
                sb.AppendLine("  warm-started by behaviour-cloning these trajectories.");
            }
            else
            {
                sb.AppendLine("  VERDICT: FAIL / MARGINAL — hindsight does not clearly beat the rigid");
                sb.AppendLine("  Hold across both models. Do NOT start RL. Adjust the premise with the");
                sb.AppendLine("  owner first: more tank freeboard, lower road severity, or add");
                sb.AppendLine("  preview/look-ahead to the observation (see docs/experiment-plan.md).");
            }

            return sb.ToString();
        }

        // ---------------------------------------------------------------- report

        public static string FormatReport(IReadOnlyList<HindsightDriveReport> reports, HindsightOptions opts)
        {
            var sb = new StringBuilder();
            sb.AppendLine("fluid-stabilizer — feasibility ceiling (offline hindsight trajectory optimisation)");
            sb.AppendLine($"generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine($"search: {opts.KnotCount} knots/axis, {opts.Restarts} restarts x {opts.IterationsPerRestart} iters, "
                          + $"rate authority {opts.RateAuthority:P0}, optimised on '{opts.OptimizeModelName}'");
            sb.AppendLine();

            foreach (var r in reports)
            {
                sb.AppendLine($"### {r.Trace}   ({r.DurationSeconds:F1} s, fill {r.FillRatio:F2}, road {(r.RoadEnabled ? "on" : "off")}, {r.Evaluations} evals)");
                sb.AppendLine("  model      Hold[L]   do-nothing[L]   ceiling[L]   d vs Hold   worstFB[m]   effort   torqueSat");
                sb.AppendLine("  " + new string('-', 100));
                foreach (var row in r.Rows)
                {
                    double delta = row.HoldSpillLitres - row.CeilingSpillLitres;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  {0,-9} {1,8:F3} {2,15:F3} {3,12:F3} {4,11:F3} {5,12:F4} {6,8:F3} {7,10:P0}",
                        row.SloshModel, row.HoldSpillLitres, row.ZeroActionSpillLitres, row.CeilingSpillLitres,
                        delta, row.CeilingWorstFreeboard, row.CeilingEffort, row.CeilingTorqueSatFraction));
                }
                sb.AppendLine();
            }

            sb.Append(Verdict(reports));
            return sb.ToString();
        }

        /// <summary>Sample the optimal trajectory at the control rate — the input for a step-4 behaviour-cloning warm start.</summary>
        public static void WriteTrajectoryCsv(TextWriter w, HindsightDriveReport report)
        {
            var ic = CultureInfo.InvariantCulture;
            var traj = GimbalRateTrajectory.FromFlat(report.BestParams, report.KnotCount, report.DurationSeconds);
            var cfg = new SimulationConfig();
            double dtControl = cfg.PhysicsStepSeconds * cfg.ControlDecimation;

            w.WriteLine("# fluid-stabilizer-hindsight-trajectory/v1");
            w.WriteLine("# trace: " + report.Trace);
            w.WriteLine("# durationSeconds: " + report.DurationSeconds.ToString("R", ic));
            w.WriteLine("# rateScaleRadPerSec: " + report.RateScaleRadPerSec.ToString("R", ic));
            w.WriteLine("# knotCount: " + report.KnotCount.ToString(ic));
            w.WriteLine("t,pitchRate,rollRate");
            for (double t = 0.0; t <= report.DurationSeconds + 1e-9; t += dtControl)
            {
                traj.Evaluate(t, out double p, out double r);
                p = Clamp(p, -1.0, 1.0) * report.RateScaleRadPerSec;
                r = Clamp(r, -1.0, 1.0) * report.RateScaleRadPerSec;
                w.WriteLine(t.ToString("F4", ic) + "," + p.ToString("R", ic) + "," + r.ToString("R", ic));
            }
        }

        // ---------------------------------------------------------------- helpers

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        // Box–Muller off a raw Pcg32 (no cached-value state to worry about for determinism).
        private static double Gaussian(Pcg32 rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
