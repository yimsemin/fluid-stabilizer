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

            int port = ArgInt(args, "-rlPort", 5555);
            double fill = ArgDouble(args, "-rlFill", profile.FillRatio);
            double seconds = ArgDouble(args, "-rlSeconds", profile.EpisodeSeconds);
            int stack = ArgInt(args, "-rlStack", 6);
            string driver = ArgStr(args, "-rlDriver", profile.Driver);
            string slosh = ArgStr(args, "-rlSlosh", profile.SloshModel);
            double briskness = ArgDouble(args, "-rlBriskness", profile.DriverIntensity);
            double aggression = ArgDouble(args, "-rlAggression", profile.DriverIntensity);
            string road = ArgStr(args, "-rlRoad", null);

            // Residual mode: the policy learns a correction on top of a baseline
            // (docs/experiment-plan.md step 4 — the F1/F4-cliff fix).
            string baseline = ArgStr(args, "-rlBaseline", "none");
            double residualFrac = ArgDouble(args, "-rlResidualFrac", 0.35);

            // Lock-capable hybrid action (decision D8): the policy's floor becomes
            // the mechanically locked tank, not a sagging servo. Defaults on
            // whenever there is a baseline; -rlLockCapable 0 forces it off.
            bool lockCapable = ArgInt(args, "-rlLockCapable", baseline != "none" ? 1 : 0) != 0;

            // Train on the owner's recorded drives instead of a synthetic driver
            // (project-goals ③). -rlTrace all | <substring>. Traces must share a
            // setup per episode, including recorded seed and full duration.
            string traceSel = ArgStr(args, "-rlTrace", null);
            List<DriverTrace> traces = LoadTraces(traceSel);

            RlEnvironment MakeEnv()
            {
                // Trace episodes resolve config/model/seed/duration together at Reset.
                // Synthetic flags are rejected for trace replay instead of silently ignored.
                if (traces.Count > 0 && new[] { "-rlFill", "-rlSeconds", "-rlSlosh", "-rlRoad", "-rlDriver", "-rlBriskness", "-rlAggression" }.Any(k => args.Contains(k)))
                    throw new ArgumentException("Trace replay cannot be combined with synthetic environment overrides.");
                SimulationConfig cfg = profile.ToConfig(seconds);
                cfg.Slosh.FillRatio = fill;
                if (road == "none") cfg.Road.Enabled = false;
                else if (road == "mild") { cfg.Road.Enabled = true; cfg.Road.Severity = 0.6; }
                else if (road == "rough") { cfg.Road.Enabled = true; cfg.Road.Severity = 1.4; }
                else if (road != null) throw new ArgumentException("Unknown -rlRoad: " + road);
                Func<ulong, IDriverInput> drv = driver switch
                {
                    "aggressive" => s => new RandomManeuverDriver(s, aggression),
                    "scripted" => _ => new ScriptedManeuverDriver(),
                    _ => s => new HighwayDriver(s, briskness),
                };
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

                var env = new RlEnvironment(cfg, drv, sl, RewardWeights.Default(), stack,
                    baselineFactory: baseFactory, residualRateScaleRadPerSec: residualScale,
                    lockCapable: lockCapable,
                    traceSelector: traces.Count == 0 ? null : (Func<ulong, DriverTrace>)(s => traces[(int)(s % (ulong)traces.Count)]));
                env.EpisodeStarted = e =>
                {
                    string path = FluidStabilizer.Core.Sim.Eval.RunProvenance.NewPath("Logs/rl-episode.txt");
                    string evidence = e.ActiveTrace != null
                        ? FluidStabilizer.Core.Sim.Eval.RunProvenance.Describe(e.ActiveTrace, e.EffectiveConfig, e.ActiveTrace.SloshModel)
                        : "code=" + FluidStabilizer.Core.Sim.Eval.RunProvenance.CodeIdentity + "\neffectiveConfig=" + ConfigSnapshot.Encode(e.EffectiveConfig);
                    System.IO.File.WriteAllText(path, evidence + "\neffectiveSeed=" + e.EffectiveSeed
                        + "\npolicy=external-training-actions (checkpoint identity unavailable)"
                        + "\nactor=" + e.ActorName + "\nstack=" + stack + "\nresidualFraction=" + residualFrac
                        + "\nlockCapable=" + lockCapable);
                };
                env.EpisodeFinished = e =>
                {
                    using var log = new FluidStabilizer.Core.Sim.Eval.EpisodeLog(
                        FluidStabilizer.Core.Sim.Eval.EpisodeLog.DefaultPath, "rl-training");
                    log.Add("rl-training", e.EffectiveConfig, e.Summary(), e.ActiveTrace);
                };
                return env;
            }

            string driverDesc = traces.Count > 0 ? $"traces({traces.Count})" : driver;
            Debug.Log($"[rl] launching TrainingServer :{port} fill={fill} driver={driverDesc} slosh={slosh} " +
                      $"road={road} episode={seconds}s stack={stack} baseline={baseline} residualFrac={residualFrac} " +
                      $"lockCapable={lockCapable}");
            new TrainingServer(MakeEnv).Serve(port);
        }

        // Membership is fixed by datasets.tsv; selector only narrows the train split.
        private static List<DriverTrace> LoadTraces(string selector)
        {
            if (string.IsNullOrEmpty(selector)) return new List<DriverTrace>();
            var traces = FluidStabilizer.Sim.TraceStore.LoadDataset("train");
            if (selector != "all") traces = traces.Where(t => System.IO.Path.GetFileName(t.SourcePath).Contains(selector)).ToList();
            if (traces.Count == 0) throw new ArgumentException("No assigned training traces match: " + selector);
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
