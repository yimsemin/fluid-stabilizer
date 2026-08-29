using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;

namespace FluidStabilizer.Rl
{
    /// <summary>
    /// Turns the offline hindsight trajectories (experiment-plan step 3,
    /// <c>Logs/hindsight/*.traj.csv</c>) into a supervised dataset for
    /// behaviour-cloning the RL policy (step C): replay each recorded drive
    /// through the <em>real</em> <see cref="RlEnvironment"/> observation pipeline
    /// while feeding it the hindsight action, and record
    /// <c>(observation, normalised action)</c> at every control tick.
    ///
    /// Plain (non-residual) mode: the target is the absolute hindsight rate ÷
    /// max rate, so a plain policy warm-started on this starts PPO from roughly
    /// the F5 ceiling instead of from noise.
    /// </summary>
    public static class BcDatasetExporter
    {
        public const string FormatId = "fluid-stabilizer-bc-dataset/v1";

        public struct Sample
        {
            public float[] Obs;      // length = 39 · frameStack
            public float ActPitch;   // normalised, [-1, 1]
            public float ActRoll;
        }

        /// <summary>One control-rate trajectory row: rad/s, as written by <c>HindsightOptimizer.WriteTrajectoryCsv</c>.</summary>
        public struct TrajRow { public double T, PitchRate, RollRate; }

        public static List<TrajRow> LoadTrajectory(TextReader r)
        {
            var ic = CultureInfo.InvariantCulture;
            var rows = new List<TrajRow>();
            string line;
            bool header = true;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (header) { header = false; continue; } // "t,pitchRate,rollRate"
                int a = line.IndexOf(',');
                int b = line.IndexOf(',', a + 1);
                rows.Add(new TrajRow
                {
                    T = double.Parse(line.Substring(0, a), ic),
                    PitchRate = double.Parse(line.Substring(a + 1, b - a - 1), ic),
                    RollRate = double.Parse(line.Substring(b + 1), ic),
                });
            }
            return rows;
        }

        public static List<Sample> FromDrive(
            DriverTrace trace, IReadOnlyList<TrajRow> traj, Func<ISloshModel> slosh, int frameStack)
        {
            SimulationConfig cfg = trace.ToConfig();
            double scale = cfg.Gimbal.MaxRateRadPerSec;
            double dtControl = cfg.PhysicsStepSeconds * cfg.ControlDecimation;
            int nTicks = (int)Math.Round(trace.DurationSeconds / dtControl);

            var env = new RlEnvironment(
                cfg,
                _ => new RecordedTraceDriver(trace),
                slosh,
                RewardWeights.Default(),
                frameStack);

            var samples = new List<Sample>(nTicks);
            float[] obs = env.Reset(trace.Seed);

            for (int i = 0; i < nTicks; i++)
            {
                TrajRow row = traj[Math.Min(i, traj.Count - 1)];
                float ap = (float)Clamp1(row.PitchRate / scale);
                float ar = (float)Clamp1(row.RollRate / scale);

                samples.Add(new Sample { Obs = obs, ActPitch = ap, ActRoll = ar });

                RlStepResult res = env.Step(ap, ar);
                obs = res.Observation;
                if (res.Terminated) break;
            }
            return samples;
        }

        public static void Write(TextWriter w, IReadOnlyList<Sample> samples,
            int obsSize, int frameStack, double actionScaleRadPerSec, IEnumerable<string> drives)
        {
            var ic = CultureInfo.InvariantCulture;
            w.WriteLine("# " + FormatId);
            w.WriteLine("# obsSize: " + obsSize.ToString(ic));
            w.WriteLine("# frameStack: " + frameStack.ToString(ic));
            w.WriteLine("# actionScaleRadPerSec: " + actionScaleRadPerSec.ToString("R", ic));
            w.WriteLine("# rows: " + samples.Count.ToString(ic));
            w.WriteLine("# drives: " + string.Join(",", drives));

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < obsSize; i++) sb.Append("o").Append(i.ToString(ic)).Append(',');
            sb.Append("aPitch,aRoll");
            w.WriteLine(sb.ToString());

            foreach (Sample s in samples)
            {
                sb.Clear();
                for (int i = 0; i < s.Obs.Length; i++)
                    sb.Append(s.Obs[i].ToString("R", ic)).Append(',');
                sb.Append(s.ActPitch.ToString("R", ic)).Append(',').Append(s.ActRoll.ToString("R", ic));
                w.WriteLine(sb.ToString());
            }
        }

        private static double Clamp1(double v) => v < -1.0 ? -1.0 : (v > 1.0 ? 1.0 : v);
    }
}
