using System;
using System.Collections.Generic;
using System.IO;
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
    /// Writes the one operating point where active stabilisation is known to help,
    /// so it can be watched in the play scene instead of only read in a table.
    ///
    /// Everything the owner recorded spills because of broadband road disturbance
    /// plus hard braking/cornering, and there the rigid tank wins (F1·F4·F5·F6·F7).
    /// The driving-premise sweep found the exception: a <b>sustained corner on a
    /// smooth road</b>, where the disturbance is low-frequency enough for a
    /// rate-limited gimbal to track it. The feasibility ceiling beats the locked
    /// tank there under <i>both</i> slosh models — the first such point in this
    /// project (findings F8).
    ///
    ///   Menu ▸ FluidStabilizer ▸ Generate Demo Drive (sustained corner)
    ///
    /// The drive lands in Traces/ as demo-corner-*.trace.csv. It is deliberately
    /// NOT added to datasets.tsv: it is a synthetic demonstration condition, not
    /// one of the owner's recorded drives (docs/reproducibility.md). Press L in
    /// the play scene to replay it and 0~4 to switch stabiliser.
    /// </summary>
    public static class DemoDriveRunner
    {
        // The winning cell, verified under pendulum + shallow water:
        // A0-F0.62-R0.0, seed 9000 (Hold 7.9 → ceiling 5.1 L pendulum,
        // 24.9 → 7.9 L shallow). Seed and steer are pinned so the demo is
        // reproducible; two of the four sweep seeds showed no win at all.
        private const ulong DemoSeed = 9000UL;
        private const double Steer = 0.45;
        private const double Fill = 0.62;
        private const double Seconds = 25.0;

        [MenuItem("FluidStabilizer/Generate Demo Drive (sustained corner)")]
        public static void RunMenu() => Execute();

        public static void Run() => Execute();

        private static void Execute()
        {
            SimulationConfig cfg = MakeConfig();
            var trace = new DriverTrace { Label = "demo-corner" };
            trace.CaptureConfig(cfg, DemoSeed, "pendulum", "Hold");

            var driver = new ConstantDriver(new DriverControls { Throttle = 0.35, Brake = 0.0, Steer = Steer });
            var loop = new SimulationLoop(cfg, new HoldController(),
                new RecordingDriver(driver, trace), new PendulumSloshModel());
            loop.RunEpisode(DemoSeed);

            Directory.CreateDirectory(TraceStore.Dir);
            string path = Path.Combine(TraceStore.Dir,
                $"demo-corner-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.trace.csv");
            trace.SaveToFile(path);

            // The ceiling only means anything for the drive it was optimised on,
            // so it is stored beside that drive; StabilizerChoice.HindsightCeiling
            // (lane ④ in the play scene) picks it up by that convention.
            var opts = new HindsightOptions
            {
                KnotCount = 8, Restarts = 3, IterationsPerRestart = 80, LockCapable = true,
                EvalModels = new List<(string, Func<ISloshModel>)>
                {
                    ("pendulum", () => new PendulumSloshModel()),
                    ("shallow", () => new ShallowWaterSloshModel()),
                },
            };
            HindsightDriveReport ceiling = HindsightOptimizer.Run(trace, opts);
            string trajPath = path.Substring(0, path.Length - ".trace.csv".Length) + ".traj.csv";
            using (var w = new StreamWriter(trajPath, false))
                HindsightOptimizer.WriteTrajectoryCsv(w, ceiling);
            Debug.Log("[demo-drive] ceiling → " + trajPath + "\n" +
                      HindsightOptimizer.FormatReport(new[] { ceiling }, opts));

            // What will the owner actually see? The ceiling is non-causal; a
            // sensor-only controller may or may not reach it. Report both so the
            // demo never implies more than it shows.
            var controllers = new List<(string, Func<SimulationConfig, IController>)>
            {
                ("① Rigid (Hold)", _ => new HoldController()),
                ("② SpecificForceAlignment", _ => new SpecificForceAlignmentController()),
                ("② PID", _ => new PidController()),
                ("② PrivilegedFeedforward", _ => new PrivilegedFullStateController()),
                ("④ Hindsight ceiling", _ => new TrajectoryPlaybackController(
                    LoadTraj(trajPath))),
            };

            List<TraceEvalRow> rows;
            using (var log = new EpisodeLog(EpisodeLog.DefaultPath, $"demo-drive {DateTime.Now:yyyy-MM-dd HH:mm}"))
                rows = TraceEvaluator.Run(new[] { trace }, controllers, TraceEvaluator.BothSloshModels(), log);

            string report = TraceEvaluator.FormatReport(rows);
            Directory.CreateDirectory("Logs");
            string output = RunProvenance.NewPath("Logs/demo-corner-eval.txt");
            File.WriteAllText(output, report);

            Debug.Log($"[demo-drive] → {path}\n[demo-drive] baselines → {output}\n\n" + report);
        }

        private static List<(double, double, double)> LoadTraj(string path)
        {
            using (var r = new StreamReader(path))
                return FluidStabilizer.Rl.BcDatasetExporter.LoadTrajectory(r)
                    .ConvertAll(x => (x.T, x.PitchRate, x.RollRate));
        }

        private static SimulationConfig MakeConfig()
        {
            var cfg = new SimulationConfig { EpisodeSeconds = Seconds };
            cfg.Slosh.FillRatio = Fill;
            cfg.Road.Enabled = false;   // the smooth-road half of the premise
            cfg.Validate();
            return cfg;
        }
    }
}
