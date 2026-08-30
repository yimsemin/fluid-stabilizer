using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Rl;
using FluidStabilizer.Sim;
using UnityEditor;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// experiment-plan step D — end-to-end check of the learned-policy bridge.
    /// Replays each recorded drive through ① the locked Hold and ③ the
    /// <see cref="RemotePolicyController"/> (served by <c>python/serve_policy.py</c>),
    /// and prints spill per drive. Start the policy server first:
    ///
    ///   python serve_policy.py --checkpoint runs/bc/actor.pt --port 5601
    ///   Unity -batchmode -quit -nographics -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.PolicyEvalRunner.Run [-ppPort 5601] [-ppStack 6]
    /// </summary>
    public static class PolicyEvalRunner
    {
        [MenuItem("FluidStabilizer/Evaluate Learned Policy (needs serve_policy.py)")]
        public static void RunMenu() => Execute();

        public static void Run() => Execute();

        private static void Execute()
        {
            string[] a = Environment.GetCommandLineArgs();
            string host = ArgStr(a, "-ppHost", "127.0.0.1");
            int port = ArgInt(a, "-ppPort", 5601);
            int stack = ArgInt(a, "-ppStack", 6);

            var traces = LoadTraces();
            if (traces.Count == 0)
            {
                Debug.LogWarning($"[policy-eval] no traces with a Logs/hindsight/*.traj.csv in {TraceStore.Dir}.");
                return;
            }

            double rateScale = new SimulationConfig().Gimbal.MaxRateRadPerSec;
            Func<IController> hold = () => new HoldController();
            Func<IController> policy = () => new RemotePolicyController(host, port, stack, rateScale, log: s => Debug.Log(s));
            var controllers = new List<(string, Func<IController>)>
            {
                ("Rigid (Hold)", hold),
                ("Learned policy", policy),
            };
            var models = new List<(string, Func<ISloshModel>)> { ("pendulum", () => new PendulumSloshModel()) };

            List<TraceEvalRow> rows;
            using (var log = new EpisodeLog(EpisodeLog.DefaultPath, $"policy-eval {DateTime.Now:yyyy-MM-dd HH:mm}"))
                rows = TraceEvaluator.Run(traces, controllers, models, log);
            string report = TraceEvaluator.FormatReport(rows);
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/policy-eval.txt", report);
            Debug.Log($"[policy-eval] → Logs/policy-eval.txt  (+ {EpisodeLog.DefaultPath})\n\n" + report);
        }

        private static List<DriverTrace> LoadTraces()
        {
            if (!Directory.Exists(TraceStore.Dir)) return new List<DriverTrace>();
            return Directory.GetFiles(TraceStore.Dir, "*" + TraceStore.Extension)
                .OrderBy(p => p)
                .Select(DriverTrace.LoadFromFile)
                .Where(t => File.Exists(Path.Combine("Logs/hindsight", t.Label + ".traj.csv")))
                .ToList();
        }

        private static string ArgStr(string[] a, string key, string def)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        private static int ArgInt(string[] a, string key, int def) =>
            int.TryParse(ArgStr(a, key, null), out int v) ? v : def;
    }
}
