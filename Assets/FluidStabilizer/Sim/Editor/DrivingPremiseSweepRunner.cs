using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
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
    /// Premise adjustment along the one axis the 2c sweep could not vary.
    ///
    /// <see cref="SweepRunner"/> overrides the tank and the road on top of the
    /// owner's recorded drives, so the *driving* is fixed — and F6 §3 found the
    /// driving, not the road, is what spills the tank. F7 then measured the other
    /// end of that axis: on a steady turn the same gimbal cuts spill by ~91%.
    /// So the winnable bracket, if it exists, lives somewhere in maneuver
    /// aggressiveness, and nothing has ever swept it.
    ///
    /// This runner synthesises drives at a grid of aggressiveness × fill and runs
    /// the same lock-capable (D8) hindsight gate on each, so the numbers are
    /// directly comparable with F6's table.
    ///
    ///   Menu ▸ FluidStabilizer ▸ Driving Premise Sweep (Hindsight Gate)
    /// or headless:
    ///   Unity -batchmode -quit -nographics -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.DrivingPremiseSweepRunner.Run \
    ///     [-dpKnots 6 -dpRestarts 2 -dpIters 50 -dpSeconds 25 -dpDrives 2 -dpShallow 0]
    ///
    /// Pendulum only by default: shallow water costs ~100x per episode, so it is
    /// opt-in and meant for verifying cells that already PASS (bracket discipline,
    /// F3 — a pendulum PASS is not a shallow-water PASS).
    /// </summary>
    public static class DrivingPremiseSweepRunner
    {
        private struct Cell
        {
            public string Id;
            public string Desc;
            public double Fill;
            public double RoadSeverity;   // 0 = road off
            public Func<ulong, IDriverInput> MakeDriver;
        }

        // Aggressiveness ladder. A0 is the F7 anchor (steady turn — known to win);
        // A3 is roughly the owner's recorded driving (known to lose). The question
        // is where between them the gate flips, and whether any fill makes that
        // crossing land in a useful place.
        private static readonly (string tag, string desc, Func<ulong, IDriverInput> make)[] Driving =
        {
            // Steer varies with the seed: ConstantDriver ignores it, but the D7 slip
            // proxy is seeded, so a fixed steer would make every "drive" in the cell
            // the same sample and hide the spread (Hold ranged 4.4-8.1 L across seeds).
            ("A0", "steady turn (F7 anchor)",
                s => new ConstantDriver(new DriverControls
                    { Throttle = 0.35, Brake = 0.0, Steer = 0.45 + 0.10 * (s % 4) })),
            ("A1", "gentle maneuvering (aggression 0.2)", s => new RandomManeuverDriver(s, 0.2)),
            ("A2", "moderate maneuvering (aggression 0.45)", s => new RandomManeuverDriver(s, 0.45)),
            ("A3", "brisk maneuvering (aggression 0.8, ~recorded)", s => new RandomManeuverDriver(s, 0.8)),
        };

        private static readonly double[] DefaultFills = { 0.57, 0.60, 0.62 };
        private static readonly double[] DefaultSeverities = { 1.0 };

        [MenuItem("FluidStabilizer/Driving Premise Sweep (Hindsight Gate)")]
        public static void RunMenu() => Execute();

        public static void Run() => Execute();

        private static void Execute()
        {
            string[] a = Environment.GetCommandLineArgs();
            int knots = ArgInt(a, "-dpKnots", 6);
            int restarts = ArgInt(a, "-dpRestarts", 2);
            int iters = ArgInt(a, "-dpIters", 50);
            double seconds = ArgDouble(a, "-dpSeconds", 25.0);
            int drivesPerCell = Math.Max(1, ArgInt(a, "-dpDrives", 2));
            bool withShallow = ArgInt(a, "-dpShallow", 0) != 0;
            double[] fills = ArgDoubles(a, "-dpFills", DefaultFills);
            double[] severities = ArgDoubles(a, "-dpSeverities", DefaultSeverities);
            string drivingPick = ArgStr(a, "-dpDriving", null);
            // D8's lock gate makes "do nothing" == the rigid tank, which is the fair
            // floor — but it also flattens the cost surface around the ES's zero
            // start (any rate inside the dead-band still locks). -dpLock 0 runs the
            // same cell without the gate to tell a flat landscape from a real wall.
            bool lockCapable = ArgInt(a, "-dpLock", 1) != 0;

            var models = new List<(string, Func<ISloshModel>)> { ("pendulum", () => new PendulumSloshModel()) };
            if (withShallow) models.Add(("shallow", () => new ShallowWaterSloshModel()));

            var grid = new List<Cell>();
            foreach (var (tag, desc, make) in Driving)
            {
                if (drivingPick != null && !drivingPick.Split(',').Select(x => x.Trim())
                        .Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (double fill in fills)
                foreach (double sev in severities)
                    grid.Add(new Cell
                    {
                        Id = $"{tag}-F{fill:0.00}-R{sev:0.0}",
                        Desc = $"{desc}, fill {fill:0.00}, road {(sev <= 0 ? "off" : sev.ToString("0.0") + "x")}",
                        Fill = fill, RoadSeverity = sev, MakeDriver = make,
                    });
            }

            Debug.Log($"[driving-sweep] {grid.Count} cells x {drivesPerCell} drive(s), " +
                      $"{knots} knots, {restarts}x{iters} search, models: " +
                      string.Join("+", models.Select(m => m.Item1)));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("fluid-stabilizer — driving-premise sweep (hindsight gate, lock-capable / D8)");
            sb.AppendLine($"generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine($"synthetic drives: {drivesPerCell} x {seconds:F0} s, " +
                          $"fills {string.Join("/", fills.Select(f => f.ToString("0.00")))}, " +
                          $"road severities {string.Join("/", severities.Select(v => v.ToString("0.0")))}");
            sb.AppendLine($"search: {knots} knots/axis, {restarts} restarts x {iters} iters, " +
                          $"lockCapable={lockCapable}, models: {string.Join("+", models.Select(m => m.Item1))}");
            sb.AppendLine();

            var summary = new List<string>
            {
                string.Format("  {0,-10} {1,-46} {2,10} {3,10} {4,9} {5,8}   {6}",
                    "id", "premise", "Hold[L]", "ceil[L]", "d[L]", "d[%]", "verdict"),
                "  " + new string('-', 116),
            };

            foreach (Cell cell in grid)
            {
                List<DriverTrace> traces = Synthesise(cell, seconds, drivesPerCell);

                var opts = new HindsightOptions
                {
                    KnotCount = knots, Restarts = restarts, IterationsPerRestart = iters,
                    LockCapable = lockCapable, EvalModels = models,
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var reports = traces.Select(t => HindsightOptimizer.Run(t, opts)).ToList();
                sw.Stop();

                string verdict = HindsightOptimizer.Verdict(reports);
                string tag = verdict.Contains("VERDICT: PASS") ? "PASS"
                    : verdict.Contains("INCONCLUSIVE") ? "INCONCLUSIVE" : "FAIL";

                double holdMean = reports.SelectMany(r => r.Rows).Average(x => x.HoldSpillLitres);
                double ceilMean = reports.SelectMany(r => r.Rows).Average(x => x.CeilingSpillLitres);
                double rel = holdMean > 1e-9 ? (holdMean - ceilMean) / holdMean : 0.0;

                summary.Add(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-10} {1,-46} {2,10:F2} {3,10:F2} {4,9:F2} {5,8:P0}   {6}",
                    cell.Id, Trim(cell.Desc, 46), holdMean, ceilMean, holdMean - ceilMean, rel, tag));

                sb.AppendLine($"════ {cell.Id} — {cell.Desc}  ({sw.Elapsed.TotalSeconds:F0}s) ════");
                sb.AppendLine(HindsightOptimizer.FormatReport(reports, opts));
                sb.AppendLine();

                Debug.Log($"[driving-sweep] {cell.Id}: {tag}  Hold~{holdMean:F2} L  ceiling~{ceilMean:F2} L  " +
                          $"({rel:P0}, {sw.Elapsed.TotalSeconds:F0}s)");
            }

            sb.Insert(0, string.Join("\n", summary) + "\n\n\n");
            Directory.CreateDirectory("Logs");
            string output = RunProvenance.NewPath("Logs/driving-premise-sweep.txt");
            File.WriteAllText(output, sb.ToString());
            Debug.Log($"[driving-sweep] → {output}\n\n" + string.Join("\n", summary));
        }

        /// <summary>
        /// Synthetic drives are recorded through the same <see cref="RecordingDriver"/>
        /// path as a human drive, so the gate sees a v2 trace with the full effective
        /// config — these are simulation conditions, never a stand-in for the owner's
        /// recorded drives (docs/reproducibility.md: they are not in datasets.tsv).
        /// </summary>
        private static List<DriverTrace> Synthesise(Cell cell, double seconds, int count)
        {
            var traces = new List<DriverTrace>();
            for (int i = 0; i < count; i++)
            {
                ulong seed = 9000UL + (ulong)i;
                var cfg = new SimulationConfig { EpisodeSeconds = seconds };
                cfg.Slosh.FillRatio = cell.Fill;
                cfg.Road.Enabled = cell.RoadSeverity > 0.0;
                cfg.Road.Severity = Math.Max(cell.RoadSeverity, 1e-6);

                var trace = new DriverTrace { Label = $"{cell.Id}-s{seed}" };
                trace.CaptureConfig(cfg, seed, "pendulum", "Hold");
                var loop = new SimulationLoop(cfg, new HoldController(),
                    new RecordingDriver(cell.MakeDriver(seed), trace), new PendulumSloshModel());
                loop.RunEpisode(seed);
                traces.Add(trace);
            }
            return traces;
        }

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

        private static string ArgStr(string[] a, string key, string def)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        private static int ArgInt(string[] a, string key, int def) =>
            int.TryParse(ArgStr(a, key, null), out int v) ? v : def;

        private static double[] ArgDoubles(string[] a, string key, double[] def)
        {
            string raw = ArgStr(a, key, null);
            if (string.IsNullOrWhiteSpace(raw)) return def;
            return raw.Split(',')
                .Select(x => double.Parse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToArray();
        }

        private static double ArgDouble(string[] a, string key, double def) =>
            double.TryParse(ArgStr(a, key, null), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
    }
}
