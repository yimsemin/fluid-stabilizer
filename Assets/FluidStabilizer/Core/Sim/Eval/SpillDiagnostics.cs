using System;
using System.Collections.Generic;
using System.Text;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim.Drivers;

namespace FluidStabilizer.Core.Sim.Eval
{
    /// <summary>One recorded drive's spill, split by how long ago the tank last felt a big horizontal push.</summary>
    public struct SpillBreakdown
    {
        public string Trace;
        public double DurationSeconds;
        public double ThresholdMps2;
        public double TotalLitres;
        public double ActiveSeconds;        // time with |a_xy| >= threshold
        public double DuringLitres;         // spill while |a_xy| >= threshold (the event itself)
        public double ShortAfterLitres;     // 0 < time since event <= ShortAfterSeconds
        public double LongAfterLitres;      // time since event > ShortAfterSeconds (ringing / never excited)
        public int SpillBursts;             // contiguous spill runs
    }

    /// <summary>
    /// MPC step 0 (experiment-plan.md): under the rigid (Hold) baseline, is the
    /// spill made at the moment of the excitation, or later by residual
    /// slosh ringing that an active controller could still damp?
    /// "Excitation" = horizontal specific force in the tank frame; the split is
    /// by time since it was last above a threshold. Uses ground truth — it is a
    /// diagnostic, not a controller input.
    /// </summary>
    public static class SpillDiagnostics
    {
        public const double ShortAfterSeconds = 1.0;

        public static SpillBreakdown Analyse(DriverTrace trace, IController controller,
            Func<DriverTrace, ISloshModel> makeSlosh, double thresholdMps2)
        {
            SimulationConfig cfg = trace.ToConfig();
            var loop = new SimulationLoop(cfg, controller, new RecordedTraceDriver(trace), makeSlosh(trace));
            loop.BeginEpisode(trace.Seed);

            double dt = cfg.PhysicsStepSeconds;
            var b = new SpillBreakdown { Trace = trace.Label, ThresholdMps2 = thresholdMps2 };
            double sinceEvent = double.PositiveInfinity;
            bool inBurst = false;
            double prevSpill = 0.0;

            while (loop.StepPhysics())
            {
                Double3 f = loop.TankState.LinearAccelerationTankFrame - loop.TankState.GravityTankFrame;
                double horiz = Math.Sqrt(f.X * f.X + f.Y * f.Y);
                bool active = horiz >= thresholdMps2;
                if (active) { sinceEvent = 0.0; b.ActiveSeconds += dt; } else sinceEvent += dt;

                double spill = loop.LatestGroundTruth.CumulativeOverflowVolume * 1000.0;
                double d = Math.Max(0.0, spill - prevSpill);
                prevSpill = spill;

                if (d > 0.0)
                {
                    if (!inBurst) b.SpillBursts++;
                    inBurst = true;
                    if (active) b.DuringLitres += d;
                    else if (sinceEvent <= ShortAfterSeconds) b.ShortAfterLitres += d;
                    else b.LongAfterLitres += d;
                }
                else inBurst = false;
            }

            b.DurationSeconds = loop.SimTime;
            b.TotalLitres = prevSpill;
            return b;
        }

        public static string Format(IReadOnlyList<SpillBreakdown> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("MPC step 0 — rigid spill breakdown by time since last excitation");
            sb.AppendLine($"excitation = tank-frame horizontal specific force ≥ threshold; 'short after' ≤ {ShortAfterSeconds:F1} s");
            sb.AppendLine();
            double lastThr = double.NaN;
            double tTot = 0, tDur = 0, tShort = 0, tLong = 0, tAct = 0, tDurS = 0;
            Action flush = () =>
            {
                if (double.IsNaN(lastThr) || tTot <= 0) return;
                sb.AppendLine(string.Format(
                    "  TOTAL    spill {0,8:F2} L   during {1,5:P0}   short-after {2,5:P0}   long-after {3,5:P0}   active time {4,5:P0}",
                    tTot, tDur / tTot, tShort / tTot, tLong / tTot, tAct / tDurS));
                sb.AppendLine();
            };
            foreach (var r in rows)
            {
                if (r.ThresholdMps2 != lastThr)
                {
                    flush();
                    lastThr = r.ThresholdMps2;
                    tTot = tDur = tShort = tLong = tAct = tDurS = 0;
                    sb.AppendLine($"### threshold {r.ThresholdMps2:F1} m/s²");
                    sb.AppendLine("  drive                      dur[s]  spill[L]  during   short-after  long-after  bursts  active%");
                }
                double t = Math.Max(r.TotalLitres, 1e-12);
                sb.AppendLine(string.Format("  {0,-24}  {1,6:F1}  {2,8:F2}  {3,6:P0}  {4,11:P0}  {5,10:P0}  {6,6}  {7,6:P0}",
                    Trunc(r.Trace, 24), r.DurationSeconds, r.TotalLitres,
                    r.DuringLitres / t, r.ShortAfterLitres / t, r.LongAfterLitres / t,
                    r.SpillBursts, r.ActiveSeconds / r.DurationSeconds));
                tTot += r.TotalLitres; tDur += r.DuringLitres; tShort += r.ShortAfterLitres;
                tLong += r.LongAfterLitres; tAct += r.ActiveSeconds; tDurS += r.DurationSeconds;
            }
            flush();
            return sb.ToString();
        }

        private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(s.Length - n);
    }
}
