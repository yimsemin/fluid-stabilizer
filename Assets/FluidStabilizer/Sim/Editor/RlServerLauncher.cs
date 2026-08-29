using System;
using System.Collections.Generic;
using System.Linq;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Rl;
using UnityEditor;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// Runs the RL <see cref="TrainingServer"/> from a headless Unity process so a
    /// Python trainer can connect (design-review §12). The env logic is all
    /// engine-free — this is just a launcher until there is a standalone .NET host.
    ///
    ///   "/path/to/Unity" -batchmode -nographics -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.RlServerLauncher.Serve \
    ///     -rlPort 5555 -rlFill 0.5 -rlDriver highway -rlSlosh pendulum -rlSeconds 30
    ///
    /// See docs/rl.md.
    /// </summary>
    public static class RlServerLauncher
    {
        public static void Serve()
        {
            string[] args = Environment.GetCommandLineArgs();

            // Defaults come from the shared experiment.txt; -rl* flags still override per-run.
            var profile = FluidStabilizer.Sim.ExperimentProfileStore.Load();
            string profRoad = profile.RoadEnabled ? (profile.RoadSeverity >= 1.2 ? "rough" : "mild") : "none";

            int port = ArgInt(args, "-rlPort", 5555);
            double fill = ArgDouble(args, "-rlFill", profile.FillRatio);
            double seconds = ArgDouble(args, "-rlSeconds", profile.EpisodeSeconds);
            int stack = ArgInt(args, "-rlStack", 6);
            string driver = ArgStr(args, "-rlDriver", profile.Driver);
            string slosh = ArgStr(args, "-rlSlosh", profile.SloshModel);
            double briskness = ArgDouble(args, "-rlBriskness", profile.DriverIntensity);
            double aggression = ArgDouble(args, "-rlAggression", profile.DriverIntensity);
            string road = ArgStr(args, "-rlRoad", profRoad);

            // Residual mode: the policy learns a correction on top of a baseline
            // (docs/experiment-plan.md step 4 — the F1/F4-cliff fix).
            string baseline = ArgStr(args, "-rlBaseline", "none");
            double residualFrac = ArgDouble(args, "-rlResidualFrac", 0.35);

            // Train on the owner's recorded drives instead of a synthetic driver
            // (project-goals ③). -rlTrace all | <substring>. Traces must share a
            // setup (fill/road) — the first match's config is used.
            string traceSel = ArgStr(args, "-rlTrace", null);
            List<DriverTrace> traces = LoadTraces(traceSel);

            RlEnvironment MakeEnv()
            {
                SimulationConfig cfg;
                if (traces.Count > 0)
                {
                    cfg = traces[0].ToConfig();
                    cfg.EpisodeSeconds = traces.Min(t => t.DurationSeconds); // no frozen tail on the shorter drives
                }
                else
                {
                    cfg = new SimulationConfig { EpisodeSeconds = seconds };
                    cfg.Slosh.FillRatio = fill;
                    if (road == "mild") { cfg.Road.Enabled = true; cfg.Road.Severity = 0.6; }
                    else if (road == "rough") { cfg.Road.Enabled = true; cfg.Road.Severity = 1.4; cfg.Road.EventsPerKm = 18.0; }
                }

                Func<ulong, IDriverInput> drv;
                if (traces.Count > 0)
                {
                    var picked = traces;
                    drv = s => new RecordedTraceDriver(picked[(int)(s % (ulong)picked.Count)]);
                }
                else
                {
                    switch (driver)
                    {
                        case "aggressive": drv = s => new RandomManeuverDriver(s, aggression); break;
                        case "scripted": drv = _ => new ScriptedManeuverDriver(); break;
                        default: drv = s => new HighwayDriver(s, briskness); break;
                    }
                }
                Func<ISloshModel> sl = slosh == "shallow"
                    ? () => new ShallowWaterSloshModel()
                    : (Func<ISloshModel>)(() => new PendulumSloshModel());

                Func<IController> baseFactory = baseline switch
                {
                    "hold" => () => new HoldController(),
                    "sfa" => () => new SpecificForceAlignmentController(),
                    "pid" => () => new PidController(),
                    _ => (Func<IController>)null,
                };
                double residualScale = baseFactory == null
                    ? double.NaN
                    : residualFrac * cfg.Gimbal.MaxRateRadPerSec;

                return new RlEnvironment(cfg, drv, sl, RewardWeights.Default(), stack,
                    baselineFactory: baseFactory, residualRateScaleRadPerSec: residualScale);
            }

            string driverDesc = traces.Count > 0 ? $"traces({traces.Count})" : driver;
            Debug.Log($"[rl] launching TrainingServer :{port} fill={fill} driver={driverDesc} slosh={slosh} " +
                      $"road={road} episode={seconds}s stack={stack} baseline={baseline} residualFrac={residualFrac}");
            new TrainingServer(MakeEnv).Serve(port);
        }

        // -rlTrace all      → every trace that has a Logs/hindsight/<label>.traj.csv
        //                     (i.e. exactly the behaviour-cloning set — keeps C1 and C3 consistent)
        // -rlTrace <substr> → traces whose filename contains <substr>
        private static List<DriverTrace> LoadTraces(string selector)
        {
            if (string.IsNullOrEmpty(selector)) return new List<DriverTrace>();
            string dir = FluidStabilizer.Sim.TraceStore.Dir;
            if (!System.IO.Directory.Exists(dir)) return new List<DriverTrace>();

            IEnumerable<string> files = System.IO.Directory
                .GetFiles(dir, "*" + FluidStabilizer.Sim.TraceStore.Extension)
                .OrderBy(p => p);
            if (selector != "all")
                files = files.Where(p => System.IO.Path.GetFileName(p).Contains(selector));

            var traces = files.Select(DriverTrace.LoadFromFile).ToList();

            if (selector == "all")
                traces = traces.Where(t =>
                    System.IO.File.Exists(System.IO.Path.Combine("Logs/hindsight", t.Label + ".traj.csv"))).ToList();

            if (traces.Count > 1)
            {
                DriverTrace a = traces[0];
                var odd = traces.Where(t =>
                    System.Math.Abs(t.FillRatio - a.FillRatio) > 1e-6 || t.RoadEnabled != a.RoadEnabled).ToList();
                if (odd.Count > 0)
                    Debug.LogWarning($"[rl] -rlTrace: {odd.Count} trace(s) have a different fill/road than "
                                     + $"'{a.Label}' — the whole batch runs under {a.Label}'s config.");
            }
            return traces;
        }

        private static string ArgStr(string[] a, string key, string def)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        private static int ArgInt(string[] a, string key, int def) =>
            int.TryParse(ArgStr(a, key, null), out int v) ? v : def;

        private static double ArgDouble(string[] a, string key, double def) =>
            double.TryParse(ArgStr(a, key, null), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;
    }
}
