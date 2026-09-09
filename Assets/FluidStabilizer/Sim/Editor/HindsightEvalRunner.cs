using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Sim;
using UnityEditor;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// experiment-plan.md step 3 — the feasibility ceiling / decision gate. For
    /// every recorded drive in <c>Traces/</c>, runs the offline hindsight
    /// trajectory search (<see cref="HindsightOptimizer"/>) and prints, per drive
    /// and per slosh model, the rigid Hold spill vs the best achievable spill.
    /// Ends with a PASS / FAIL verdict on whether RL is worth starting.
    ///
    ///   Menu ▸ FluidStabilizer ▸ Feasibility Ceiling (Hindsight)
    ///
    /// or headless:
    ///   Unity -batchmode -quit -nographics -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.HindsightEvalRunner.Run \
    ///     -hsKnots 8 -hsRestarts 3 -hsIters 80 -hsAuthority 1.0 -hsShallow 1
    ///
    /// 묶음 3 gate re-run: -hsLockCapable 1 gives the hindsight actor the
    /// lock-capable hybrid action (D8). The 2c premise sweep overrides the tank /
    /// road on top of each drive's recorded setup:
    ///     -hsFill 0.55 -hsTankHeight 0.60 -hsRoadSeverity 0.6 -hsGrip 0.6
    ///
    /// Writes Logs/hindsight-eval.txt and Logs/hindsight/<drive>.traj.csv
    /// (the optimal trajectories — the input for a step-4 behaviour-cloning warm start).
    /// </summary>
    public static class HindsightEvalRunner
    {
        [MenuItem("FluidStabilizer/Feasibility Ceiling (Hindsight)")]
        public static void RunMenu() => Execute(FromArgsOrDefaults());

        // -executeMethod entry point.
        public static void Run() => Execute(FromArgsOrDefaults());

        private static void Execute(HindsightOptions opts)
        {
            string[] a = Environment.GetCommandLineArgs();
            bool allTraces = ArgInt(a, "-hsAllTraces", 0) != 0;
            double minFill = ArgDouble(a, "-hsMinFill", 0.60);
            int maxDrives = ArgInt(a, "-hsMaxDrives", 0); // 0 = no cap

            List<DriverTrace> traces = LoadTraces();
            int found = traces.Count;
            if (!allTraces)
                traces = traces.Where(t => t.RoadEnabled && t.FillRatio >= minFill - 1e-9).ToList();
            if (maxDrives > 0 && traces.Count > maxDrives)
                traces = traces.Take(maxDrives).ToList();

            if (traces.Count == 0)
            {
                Debug.LogWarning($"[hindsight] {found} trace(s) in {TraceStore.Dir}, none discriminating " +
                                 $"(need road on + fill >= {minFill:F2}). Record some, or pass -hsAllTraces 1.");
                return;
            }
            Debug.Log($"[hindsight] {traces.Count} of {found} trace(s) selected " +
                      (allTraces ? "(all)" : $"(road on, fill >= {minFill:F2})"));

            Debug.Log($"[hindsight] {traces.Count} drive(s), {opts.KnotCount} knots/axis, " +
                      $"{opts.Restarts}x{opts.IterationsPerRestart} search, models: " +
                      string.Join("+", opts.EvalModels.Select(m => m.name)) +
                      $", lockCapable={opts.LockCapable}, override={(opts.ConfigOverride != null ? "yes" : "no")}" +
                      " — this takes a few minutes.");

            string outputDir = RunProvenance.NewPath("Logs/hindsight");
            Directory.CreateDirectory(outputDir);
            var reports = new List<HindsightDriveReport>();
            foreach (DriverTrace trace in traces)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                HindsightDriveReport r = HindsightOptimizer.Run(trace, opts);
                sw.Stop();
                reports.Add(r);

                double bestDelta = r.Rows.Count > 0 ? r.Rows.Max(x => x.HoldSpillLitres - x.CeilingSpillLitres) : 0.0;
                Debug.Log($"[hindsight] {trace.Label}: {r.Evaluations} evals in {sw.Elapsed.TotalSeconds:F0}s, " +
                          $"best Hold-ceiling = {bestDelta:F2} L");

                WriteTrajectory(r, outputDir);
            }

            string report = HindsightOptimizer.FormatReport(reports, opts);
            Directory.CreateDirectory("Logs");
            string output = Path.Combine(outputDir, "report.txt");
            File.WriteAllText(output, report);
            Debug.Log("[hindsight] → " + output + "\n\n" + report);
        }

        private static void WriteTrajectory(HindsightDriveReport r, string outputDir)
        {
            using (var w = new StreamWriter(Path.Combine(outputDir, r.Trace + ".traj.csv"), false))
                HindsightOptimizer.WriteTrajectoryCsv(w, r);
        }

        private static List<DriverTrace> LoadTraces() => TraceStore.LoadDataset("train");

        private static HindsightOptions FromArgsOrDefaults()
        {
            string[] a = Environment.GetCommandLineArgs();
            var opts = new HindsightOptions
            {
                KnotCount = ArgInt(a, "-hsKnots", 8),
                Restarts = ArgInt(a, "-hsRestarts", 3),
                IterationsPerRestart = ArgInt(a, "-hsIters", 80),
                RateAuthority = ArgDouble(a, "-hsAuthority", 1.0),
            };

            bool withShallow = ArgInt(a, "-hsShallow", 1) != 0;
            opts.EvalModels = new List<(string, Func<FluidStabilizer.Contracts.ISloshModel>)>
            {
                ("pendulum", () => new PendulumSloshModel()),
            };
            if (withShallow)
                opts.EvalModels.Add(("shallow", () => new ShallowWaterSloshModel()));

            opts.LockCapable = ArgInt(a, "-hsLockCapable", 0) != 0;
            opts.ConfigOverride = BuildConfigOverride(a);

            return opts;
        }

        /// <summary>
        /// The 2c premise sweep (experiment-plan). Each flag overrides one knob on
        /// top of the drive's recorded setup; absent flags leave that knob alone.
        /// Same recorded driver inputs — a different tank / road.
        /// </summary>
        private static Action<FluidStabilizer.Core.Sim.SimulationConfig> BuildConfigOverride(string[] a)
        {
            double? fill = ArgNullableDouble(a, "-hsFill");
            double? tankH = ArgNullableDouble(a, "-hsTankHeight");
            double? sev = ArgNullableDouble(a, "-hsRoadSeverity");
            double? curve = ArgNullableDouble(a, "-hsCurve");
            double? hills = ArgNullableDouble(a, "-hsHills");
            double? grip = ArgNullableDouble(a, "-hsGrip");
            if (fill == null && tankH == null && sev == null && curve == null && hills == null && grip == null)
                return null;

            return cfg =>
            {
                if (fill != null) cfg.Slosh.FillRatio = fill.Value;
                if (tankH != null) cfg.Slosh.TankHeightZ = tankH.Value;
                if (sev != null) { cfg.Road.Enabled = true; cfg.Road.Severity = sev.Value; }
                if (curve != null) cfg.Road.CurvatureAmplitudeRadPerM *= Math.Max(0.0, curve.Value);
                if (hills != null) cfg.Road.GradeAmplitudeRad *= Math.Max(0.0, hills.Value);
                if (grip != null)
                {
                    double g = Math.Max(0.15, Math.Min(1.5, grip.Value));
                    cfg.Vehicle.FrictionLimit *= g;
                    cfg.Vehicle.MaxBrake *= g;
                    cfg.Vehicle.MaxAccel *= g;
                }
            };
        }

        private static double? ArgNullableDouble(string[] a, string key)
        {
            string s = ArgStr(a, key, null);
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
        }

        private static string ArgStr(string[] a, string key, string def)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        private static int ArgInt(string[] a, string key, int def) =>
            int.TryParse(ArgStr(a, key, null), out int v) ? v : def;

        private static double ArgDouble(string[] a, string key, double def) =>
            double.TryParse(ArgStr(a, key, null), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
    }
}
