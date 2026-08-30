using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Sim;
using UnityEditor;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// experiment-plan.md 묶음 2c — the premise sweep. Re-runs the F5 feasibility
    /// gate (<see cref="HindsightOptimizer"/>) at a small grid of operating points,
    /// all with the lock-capable hybrid action (decision D8), and reports for each
    /// point whether hindsight now meaningfully beats the rigid locked tank under
    /// both slosh models. The output picks the training condition for 묶음 3.
    ///
    ///   Menu ▸ FluidStabilizer ▸ Premise Sweep 2c (Hindsight Gate)
    /// or headless:
    ///   Unity -batchmode -quit -nographics -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.SweepRunner.Run \
    ///     [-swKnots 6 -swRestarts 2 -swIters 50 -swShallow 1 -swOnly S0,S3]
    ///
    /// Writes Logs/sweep-2c.txt. Reduced search budget by default — the gate is a
    /// direction question, not a last-litre squeeze (finding F5: the ceiling
    /// plateaus quickly).
    /// </summary>
    public static class SweepRunner
    {
        private struct Condition
        {
            public string Id;
            public string Desc;
            public Action<SimulationConfig> Override;
        }

        private static readonly List<Condition> Grid = new List<Condition>
        {
            new Condition { Id = "S0", Desc = "lock-capable only (recorded operating point)",
                Override = null },
            new Condition { Id = "S1", Desc = "fill 0.55 (more freeboard)",
                Override = c => c.Slosh.FillRatio = 0.55 },
            new Condition { Id = "S2", Desc = "fill 0.50 (lots of freeboard)",
                Override = c => c.Slosh.FillRatio = 0.50 },
            new Condition { Id = "S3", Desc = "road severity 0.6x (gentler road)",
                Override = c => { c.Road.Enabled = true; c.Road.Severity = 0.6; } },
            new Condition { Id = "S4", Desc = "fill 0.55 + road severity 0.6x",
                Override = c => { c.Slosh.FillRatio = 0.55; c.Road.Enabled = true; c.Road.Severity = 0.6; } },
            new Condition { Id = "S5", Desc = "grip 0.6 (wet — rigid tank can't reject it)",
                Override = c => Grip(c, 0.6) },
            new Condition { Id = "S6", Desc = "fill 0.55 + grip 0.6",
                Override = c => { c.Slosh.FillRatio = 0.55; Grip(c, 0.6); } },
            new Condition { Id = "S7", Desc = "fill 0.50 + road severity 0.6x (easiest — sanity ceiling)",
                Override = c => { c.Slosh.FillRatio = 0.50; c.Road.Enabled = true; c.Road.Severity = 0.6; } },

            // Fine probe of the razor's edge between "rigid dry" (fill 0.55) and
            // "rigid spills, unbeatable" (fill 0.62) — where a winnable bracket
            // would have to live if it exists at all.
            new Condition { Id = "S8", Desc = "fill 0.60 (probe)", Override = c => c.Slosh.FillRatio = 0.60 },
            new Condition { Id = "S9", Desc = "fill 0.58 (probe)", Override = c => c.Slosh.FillRatio = 0.58 },
            new Condition { Id = "S10", Desc = "fill 0.57 (probe)", Override = c => c.Slosh.FillRatio = 0.57 },
        };

        private static void Grip(SimulationConfig c, double g)
        {
            g = Math.Max(0.15, Math.Min(1.5, g));
            c.Vehicle.FrictionLimit *= g;
            c.Vehicle.MaxBrake *= g;
            c.Vehicle.MaxAccel *= g;
        }

        [MenuItem("FluidStabilizer/Premise Sweep 2c (Hindsight Gate)")]
        public static void RunMenu() => Execute();

        public static void Run() => Execute();

        private static void Execute()
        {
            string[] a = Environment.GetCommandLineArgs();
            int knots = ArgInt(a, "-swKnots", 6);
            int restarts = ArgInt(a, "-swRestarts", 2);
            int iters = ArgInt(a, "-swIters", 50);
            bool withShallow = ArgInt(a, "-swShallow", 1) != 0;
            string only = ArgStr(a, "-swOnly", null);
            HashSet<string> pick = only == null ? null
                : new HashSet<string>(only.Split(',').Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);

            List<DriverTrace> traces = LoadCeilingTraces();
            if (traces.Count == 0)
            {
                Debug.LogWarning($"[sweep] no recorded drive in {TraceStore.Dir} has a Logs/hindsight/*.traj.csv. " +
                                 "Run the Feasibility Ceiling first.");
                return;
            }

            var models = new List<(string, Func<ISloshModel>)> { ("pendulum", () => new PendulumSloshModel()) };
            if (withShallow) models.Add(("shallow", () => new ShallowWaterSloshModel()));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("fluid-stabilizer — 묶음 2c premise sweep (F5 gate re-run, lock-capable / D8)");
            sb.AppendLine($"generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine($"drives: {traces.Count}  ({string.Join(", ", traces.Select(t => t.Label))})");
            sb.AppendLine($"search: {knots} knots/axis, {restarts} restarts x {iters} iters, models: {string.Join("+", models.Select(m => m.Item1))}");
            sb.AppendLine();

            var summary = new List<string>();
            summary.Add(string.Format("  {0,-4} {1,-46} {2,10} {3,10} {4,9}   {5}",
                "id", "premise", "Hold[L]", "ceil[L]", "d[L]", "verdict"));
            summary.Add("  " + new string('-', 104));

            foreach (Condition cond in Grid)
            {
                if (pick != null && !pick.Contains(cond.Id)) continue;

                var opts = new HindsightOptions
                {
                    KnotCount = knots,
                    Restarts = restarts,
                    IterationsPerRestart = iters,
                    LockCapable = true,
                    ConfigOverride = cond.Override,
                    EvalModels = models,
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var reports = new List<HindsightDriveReport>();
                foreach (DriverTrace t in traces)
                    reports.Add(HindsightOptimizer.Run(t, opts));
                sw.Stop();

                string verdict = HindsightOptimizer.Verdict(reports);
                string tag = verdict.Contains("VERDICT: PASS") ? "PASS"
                    : verdict.Contains("INCONCLUSIVE") ? "INCONCLUSIVE" : "FAIL";

                // means across drives × models
                double holdMean = reports.SelectMany(r => r.Rows).Average(x => x.HoldSpillLitres);
                double ceilMean = reports.SelectMany(r => r.Rows).Average(x => x.CeilingSpillLitres);

                summary.Add(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-4} {1,-46} {2,10:F2} {3,10:F2} {4,9:F2}   {5}",
                    cond.Id, Trim(cond.Desc, 46), holdMean, ceilMean, holdMean - ceilMean, tag));

                sb.AppendLine($"════ {cond.Id} — {cond.Desc}  ({sw.Elapsed.TotalSeconds:F0}s) ════");
                sb.AppendLine(HindsightOptimizer.FormatReport(reports, opts));
                sb.AppendLine();

                Debug.Log($"[sweep] {cond.Id}: {tag}  Hold~{holdMean:F1} L  ceiling~{ceilMean:F1} L  ({sw.Elapsed.TotalSeconds:F0}s)");
            }

            sb.Insert(0, string.Join("\n", summary) + "\n\n\n");
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/sweep-2c.txt", sb.ToString());
            Debug.Log("[sweep] → Logs/sweep-2c.txt\n\n" + string.Join("\n", summary));
        }

        private static List<DriverTrace> LoadCeilingTraces()
        {
            if (!Directory.Exists(TraceStore.Dir)) return new List<DriverTrace>();
            return Directory.GetFiles(TraceStore.Dir, "*" + TraceStore.Extension)
                .OrderBy(p => p)
                .Select(DriverTrace.LoadFromFile)
                .Where(t => File.Exists(Path.Combine("Logs/hindsight", t.Label + ".traj.csv")))
                .ToList();
        }

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

        private static string ArgStr(string[] a, string key, string def)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        private static int ArgInt(string[] a, string key, int def) =>
            int.TryParse(ArgStr(a, key, null), out int v) ? v : def;
    }
}
