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
                Debug.LogWarning($"[policy-eval] no assigned traces in {TraceStore.Dir}.");
                return;
            }

            // rateScale comes from the drive being replayed, not from defaults —
            // a trace recorded with a non-default gimbal rate would otherwise be
            // served commands scaled to the wrong ceiling.
            Func<SimulationConfig, IController> hold = _ => new HoldController();
            Func<SimulationConfig, IController> policy = cfg => new RemotePolicyController(
                host, port, stack, cfg.Gimbal.MaxRateRadPerSec, log: s => Debug.Log(s), gimbal: cfg.Gimbal);
            var controllers = new List<(string, Func<SimulationConfig, IController>)>
            {
                ("Rigid (Hold)", hold),
                ("Learned policy", policy),
            };
            List<(string, Func<ISloshModel>)> models = null;

            List<TraceEvalRow> rows;
            using (var log = new EpisodeLog(EpisodeLog.DefaultPath, $"policy-eval {DateTime.Now:yyyy-MM-dd HH:mm}"))
                rows = TraceEvaluator.Run(traces, controllers, models, log);
            string report = TraceEvaluator.FormatReport(rows);
            Directory.CreateDirectory("Logs");
            string output = RunProvenance.NewPath("Logs/policy-eval.txt");
            File.WriteAllText(output, report);
            Debug.Log($"[policy-eval] → {output}  (+ {EpisodeLog.DefaultPath})\n\n" + report);
        }

        private static List<DriverTrace> LoadTraces() => TraceStore.LoadDataset();

        private static string ArgStr(string[] a, string key, string def)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        private static int ArgInt(string[] a, string key, int def) =>
            int.TryParse(ArgStr(a, key, null), out int v) ? v : def;
    }
}
