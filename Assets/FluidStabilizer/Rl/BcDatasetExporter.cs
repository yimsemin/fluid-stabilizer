using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
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
            public float[] Obs;      // length = SingleFrameSize · frameStack
            public float ActPitch;   // normalised, [-1, 1]
            public float ActRoll;
            /// <summary>Explicit-lock contracts only: the release request (findings F9).
            /// The hindsight teacher is an always-active trajectory, so it releases.</summary>
            public float ActLock;
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

        /// <summary>
        /// Turn a teacher trajectory (absolute joint rates) into supervised targets
        /// <b>in the action space the student will actually be trained and served
        /// in</b>.
        ///
        /// Plain contract: target = teacherRate / actionScale — the absolute action.
        /// Residual contract: target = (teacherRate − baselineRate) / actionScale,
        /// i.e. the correction the composer needs to reproduce the teacher on top of
        /// its baseline. Cloning absolute targets into a residual policy would teach
        /// it to command the teacher's rate *plus* the baseline's, which is a
        /// different trajectory entirely.
        ///
        /// Targets that do not fit the residual range are clamped, and the fraction
        /// clamped is reported so an under-scaled residual band is visible instead of
        /// silently truncating the teacher.
        /// </summary>
        public static List<Sample> FromDrive(
            DriverTrace trace, IReadOnlyList<TrajRow> traj, Func<ISloshModel> slosh, int frameStack,
            string baselineId = null, double residualScaleRadPerSec = double.NaN,
            bool lockCapable = false, string lockMode = "deadband")
        {
            SimulationConfig cfg = trace.ToConfig();
            double dtControl = cfg.PhysicsStepSeconds * cfg.ControlDecimation;
            int nTicks = (int)Math.Round(trace.DurationSeconds / dtControl);

            bool residual = !string.IsNullOrEmpty(baselineId)
                            && !baselineId.Equals("none", StringComparison.OrdinalIgnoreCase);

            var env = new RlEnvironment(
                cfg,
                _ => new RecordedTraceDriver(trace),
                slosh,
                RewardWeights.Default(),
                frameStack,
                baselineFactory: residual ? () => PolicyContract.MakeBaseline(baselineId) : (Func<IController>)null,
                residualRateScaleRadPerSec: residualScaleRadPerSec,
                lockCapable: lockCapable,
                baselineId: residual ? baselineId : null,
                lockMode: lockMode);

            double scale = env.Contract.ActionScaleRadPerSec;
            IController baseline = residual ? PolicyContract.MakeBaseline(baselineId) : null;
            baseline?.Reset();

            var samples = new List<Sample>(nTicks);
            float[] obs = env.Reset(trace.Seed);
            int clamped = 0;

            for (int i = 0; i < nTicks; i++)
            {
                TrajRow row = traj[Math.Min(i, traj.Count - 1)];
                double targetP = row.PitchRate, targetR = row.RollRate;

                if (baseline != null)
                {
                    SensorBundle b = env.NextControlBundle;
                    StabilizerCommand cmd = baseline.ComputeCommand(b);
                    ActionComposer.BaselineRates(cmd, b, out double bp, out double br);
                    targetP -= bp;
                    targetR -= br;
                }

                double rawP = targetP / scale, rawR = targetR / scale;
                if (Math.Abs(rawP) > 1.0 || Math.Abs(rawR) > 1.0) clamped++;
                float ap = (float)Clamp1(rawP);
                float ar = (float)Clamp1(rawR);

                // The teacher runs the servo the whole way, so under an explicit-lock
                // contract its release request is a committed +1 (findings F9).
                float al = env.Contract.IsExplicitLock ? 1f : 0f;
                samples.Add(new Sample { Obs = obs, ActPitch = ap, ActRoll = ar, ActLock = al });

                RlStepResult res = env.Step(ap, ar, al);
                obs = res.Observation;
                if (res.Terminated) break;
            }

            LastClampedFraction = samples.Count == 0 ? 0.0 : (double)clamped / samples.Count;
            LastContract = env.Contract;
            return samples;
        }

        /// <summary>Fraction of the most recent drive's targets that hit the action limit.</summary>
        public static double LastClampedFraction { get; private set; }

        /// <summary>The contract the most recent drive's targets are expressed in.</summary>
        public static PolicyContract LastContract { get; private set; }

        public static void Write(TextWriter w, IReadOnlyList<Sample> samples,
            int obsSize, int frameStack, double actionScaleRadPerSec, IEnumerable<string> drives,
            PolicyContract contract = default, double clampedFraction = 0.0)
        {
            var ic = CultureInfo.InvariantCulture;
            w.WriteLine("# " + FormatId);
            w.WriteLine("# obsSize: " + obsSize.ToString(ic));
            w.WriteLine("# frameStack: " + frameStack.ToString(ic));
            w.WriteLine("# actionScaleRadPerSec: " + actionScaleRadPerSec.ToString("R", ic));
            w.WriteLine("# rows: " + samples.Count.ToString(ic));
            w.WriteLine("# drives: " + string.Join(",", drives));
            // What these targets mean. bc_pretrain carries it into the checkpoint so
            // serve_policy can hand it back to Unity (PolicyContract).
            w.WriteLine("# contract: " + (contract.ActionScaleRadPerSec > 0
                ? contract.Encode()
                : PolicyContract.Legacy(actionScaleRadPerSec, frameStack).Encode()));
            w.WriteLine("# clampedFraction: " + clampedFraction.ToString("R", ic));

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < obsSize; i++) sb.Append("o").Append(i.ToString(ic)).Append(',');
            bool withLock = contract.IsExplicitLock;
            sb.Append(withLock ? "aPitch,aRoll,aLock" : "aPitch,aRoll");
            w.WriteLine(sb.ToString());

            foreach (Sample s in samples)
            {
                sb.Clear();
                for (int i = 0; i < s.Obs.Length; i++)
                    sb.Append(s.Obs[i].ToString("R", ic)).Append(',');
                sb.Append(s.ActPitch.ToString("R", ic)).Append(',').Append(s.ActRoll.ToString("R", ic));
                if (withLock) sb.Append(',').Append(s.ActLock.ToString("R", ic));
                w.WriteLine(sb.ToString());
            }
        }

        private static double Clamp1(double v) => v < -1.0 ? -1.0 : (v > 1.0 ? 1.0 : v);
    }
}
