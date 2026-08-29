using System;
using System.Linq;
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
            int port = ArgInt(args, "-rlPort", 5555);
            double fill = ArgDouble(args, "-rlFill", 0.5);
            double seconds = ArgDouble(args, "-rlSeconds", 30.0);
            int stack = ArgInt(args, "-rlStack", 6);
            string driver = ArgStr(args, "-rlDriver", "highway");
            string slosh = ArgStr(args, "-rlSlosh", "pendulum");
            double briskness = ArgDouble(args, "-rlBriskness", 0.6);
            double aggression = ArgDouble(args, "-rlAggression", 0.7);
            string road = ArgStr(args, "-rlRoad", "none");

            RlEnvironment MakeEnv()
            {
                var cfg = new SimulationConfig { EpisodeSeconds = seconds };
                cfg.Slosh.FillRatio = fill;
                if (road == "mild") { cfg.Road.Enabled = true; cfg.Road.Severity = 0.6; }
                else if (road == "rough") { cfg.Road.Enabled = true; cfg.Road.Severity = 1.4; cfg.Road.EventsPerKm = 18.0; }

                Func<ulong, FluidStabilizer.Contracts.IDriverInput> drv = driver switch
                {
                    "aggressive" => s => new RandomManeuverDriver(s, aggression),
                    "scripted" => _ => new ScriptedManeuverDriver(),
                    _ => s => new HighwayDriver(s, briskness),
                };
                Func<FluidStabilizer.Contracts.ISloshModel> sl = slosh == "shallow"
                    ? () => new ShallowWaterSloshModel()
                    : (Func<FluidStabilizer.Contracts.ISloshModel>)(() => new PendulumSloshModel());

                return new RlEnvironment(cfg, drv, sl, RewardWeights.Default(), stack);
            }

            Debug.Log($"[rl] launching TrainingServer :{port} fill={fill} driver={driver} slosh={slosh} " +
                      $"road={road} episode={seconds}s stack={stack}");
            new TrainingServer(MakeEnv).Serve(port);
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
