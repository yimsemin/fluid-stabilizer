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
                      " — this takes a few minutes.");

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

                WriteTrajectory(r);
            }

            string report = HindsightOptimizer.FormatReport(reports, opts);
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/hindsight-eval.txt", report);
            Debug.Log("[hindsight] → Logs/hindsight-eval.txt\n\n" + report);
        }

        private static void WriteTrajectory(HindsightDriveReport r)
        {
            Directory.CreateDirectory("Logs/hindsight");
            using (var w = new StreamWriter(Path.Combine("Logs/hindsight", r.Trace + ".traj.csv"), false))
                HindsightOptimizer.WriteTrajectoryCsv(w, r);
        }

        private static List<DriverTrace> LoadTraces()
        {
            if (!Directory.Exists(TraceStore.Dir)) return new List<DriverTrace>();
            return Directory.GetFiles(TraceStore.Dir, "*" + TraceStore.Extension)
                .OrderBy(p => p)
                .Select(DriverTrace.LoadFromFile)
                .ToList();
        }

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

            return opts;
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
