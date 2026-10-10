using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    /// experiment-plan A1 — teacher diversification. The F10 policy was a clone of
    /// ONE drive (<see cref="DemoDriveRunner"/>), so generalisation was untested.
    /// This writes a family of F8-premise drives (smooth road, sustained corner) that
    /// differ in steer, throttle, corner timing and an optional counter-steer, each
    /// with its own hindsight-ceiling trajectory beside it, plus a manifest that
    /// splits them into train / validation.
    ///
    ///   -executeMethod FluidStabilizer.SimEditor.MultiCornerRunner.Run
    ///       [-mcTrain 8] [-mcValidation 4] [-mcSeedBase 9100]
    ///
    /// Synthetic by construction — NOT the owner's recorded drives, so it writes its
    /// own manifest (<c>datasets-f8-multi.tsv</c>) and never touches datasets.tsv.
    /// </summary>
    public static class MultiCornerRunner
    {
        private const double Fill = 0.62;
        private const double Seconds = 25.0;
        // Gate: the teacher must beat Hold by this much (both) to count as "active".
        private const double GateMinLitres = 0.5;
        private const double GateMinFraction = 0.10;
        public const string ManifestPath = "datasets-f8-multi.tsv";

        [MenuItem("FluidStabilizer/Generate Multi-Corner Teachers (F8 family)")]
        public static void RunMenu() => Execute();

        public static void Run() => Execute();

        /// <summary>Corner timing + magnitude, all derived from the seed.</summary>
        public sealed class CornerDriver : IDriverInput
        {
            public double Throttle, Steer1, Steer2, SwitchAt, RampSeconds;
            public string Name => "MultiCorner";
            public void Reset() { }

            public DriverControls Sample(double t)
            {
                // smooth ramp 0 → Steer1 over RampSeconds, then → Steer2 at SwitchAt
                double s = Steer1 * Smooth(t / RampSeconds);
                if (t > SwitchAt) s = Lerp(Steer1, Steer2, Smooth((t - SwitchAt) / RampSeconds));
                return new DriverControls { Throttle = Throttle, Brake = 0.0, Steer = s };
            }

            private static double Smooth(double x)
            {
                x = Math.Max(0.0, Math.Min(1.0, x));
                return x * x * (3.0 - 2.0 * x);
            }

            private static double Lerp(double a, double b, double x) => a + (b - a) * x;

            public static CornerDriver FromSeed(ulong seed)
            {
                var r = new System.Random(unchecked((int)(seed * 2654435761UL)));
                double U(double lo, double hi) => lo + (hi - lo) * r.NextDouble();
                double sign = r.NextDouble() < 0.5 ? -1.0 : 1.0;
                double s1 = sign * U(0.30, 0.80);
                // half the drives hold one corner; half change to a different one
                bool change = r.NextDouble() < 0.5;
                double s2 = change ? sign * U(0.30, 0.80) * (r.NextDouble() < 0.4 ? -1.0 : 1.0) : s1;
                return new CornerDriver
                {
                    Throttle = U(0.25, 0.45), Steer1 = s1, Steer2 = s2,
                    SwitchAt = U(10.0, 16.0), RampSeconds = U(1.0, 3.0),
                };
            }
        }

        private static void Execute()
        {
            string[] a = Environment.GetCommandLineArgs();
            int nTrain = ArgInt(a, "-mcTrain", 8);
            int nVal = ArgInt(a, "-mcValidation", 4);
            ulong seedBase = (ulong)ArgInt(a, "-mcSeedBase", 9100);

            var opts = new HindsightOptions
            {
                KnotCount = 8, Restarts = 3, IterationsPerRestart = 80, LockCapable = true,
                EvalModels = new List<(string, Func<ISloshModel>)>
                {
                    ("pendulum", () => new PendulumSloshModel()),
                    ("shallow", () => new ShallowWaterSloshModel()),
                },
            };

            Directory.CreateDirectory(TraceStore.Dir);
            var manifest = new StringBuilder();
            manifest.AppendLine("# fluid-stabilizer-datasets/v1");
            manifest.AppendLine("# F8 계열 합성 코너링 (교사 다양화, experiment-plan A1). 소유자의 녹화 주행이 아니다 — datasets.tsv 와 별개.");
            manifest.AppendLine("split\tfile\tsourceSha256\thistory");
            var report = new StringBuilder();

            for (int i = 0; i < nTrain + nVal; i++)
            {
                ulong seed = seedBase + (ulong)i;
                string split = i < nTrain ? "train" : "validation";
                CornerDriver driver = CornerDriver.FromSeed(seed);

                SimulationConfig cfg = new SimulationConfig { EpisodeSeconds = Seconds };
                cfg.Slosh.FillRatio = Fill;
                cfg.Road.Enabled = false;
                cfg.Validate();

                var trace = new DriverTrace { Label = $"f8multi-s{seed}" };
                trace.CaptureConfig(cfg, seed, "pendulum", "Hold");
                var loop = new SimulationLoop(cfg, new HoldController(),
                    new RecordingDriver(driver, trace), new PendulumSloshModel());
                loop.RunEpisode(seed);

                string path = Path.Combine(TraceStore.Dir, $"f8multi-s{seed}.trace.csv");
                if (File.Exists(path)) throw new IOException("Refusing to overwrite " + path);
                trace.SaveToFile(path);

                HindsightDriveReport ceiling = HindsightOptimizer.Run(trace, opts);
                // A teacher that barely beats the rigid tank is noise, not a lesson:
                // cloning it teaches "release the lock" where locked is right. Below
                // the gate the trajectory is replaced by the exact do-nothing floor,
                // which BcDatasetExporter reads as "stay locked" (findings F11).
                bool gated = false;
                foreach (HindsightModelRow row in ceiling.Rows)
                {
                    if (row.SloshModel != ceiling.OptimizeModel) continue;
                    double gain = row.HoldSpillLitres - row.CeilingSpillLitres;
                    gated = gain < Math.Max(GateMinLitres, GateMinFraction * row.HoldSpillLitres);
                }
                if (gated) ceiling.BestParams = new double[ceiling.BestParams.Length];
                string trajPath = path.Substring(0, path.Length - ".trace.csv".Length) + ".traj.csv";
                using (var w = new StreamWriter(trajPath, false))
                    HindsightOptimizer.WriteTrajectoryCsv(w, ceiling);

                manifest.AppendLine($"{split}\t{Path.GetFileName(path)}\t{ConfigSnapshot.FileHash(path)}\tsynthetic-f8-multi");
                string line = $"[mc] {split} s{seed} {(gated ? "IDLE" : "ACTIVE")} thr={driver.Throttle:F2} steer1={driver.Steer1:F2} " +
                              $"steer2={driver.Steer2:F2} switch={driver.SwitchAt:F1}s";
                report.AppendLine(line);
                Debug.Log(line + "\n" + HindsightOptimizer.FormatReport(new[] { ceiling }, opts));
            }

            File.WriteAllText(ManifestPath, manifest.ToString());
            string rp = RunProvenance.NewPath("Logs/multi-corner.txt");
            Directory.CreateDirectory("Logs");
            File.WriteAllText(rp, report.ToString());
            Debug.Log($"[mc] manifest → {ManifestPath}; drives → {TraceStore.Dir}; summary → {rp}");
        }

        private static int ArgInt(string[] a, string key, int def)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out int v) ? v : def;
        }
    }
}
