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
using FluidStabilizer.Sim;
using UnityEditor;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// Runs the recorded-drive evaluation (experiment-plan.md step 2c) — every
    /// trace in <c>Traces/</c> through the analytic baselines, printed per drive.
    /// The learned policy joins once an ONNX inference path exists (phase 1.5).
    ///
    ///   Menu ▸ FluidStabilizer ▸ Evaluate Recorded Drives  (pendulum ROM only)
    ///   Menu ▸ FluidStabilizer ▸ Evaluate Recorded Drives (+ shallow water, slow)
    ///
    /// or headless:
    ///   Unity -batchmode -quit -nographics -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.TraceEvalRunner.Run
    ///
    /// Writes Logs/trace-eval.txt and echoes it to the Console.
    /// </summary>
    public static class TraceEvalRunner
    {
        private static readonly (string, Func<SimulationConfig, IController>)[] Baselines =
        {
            ("Rigid (Hold)", _ => new HoldController()),
            ("SpecificForceAlignment", _ => new SpecificForceAlignmentController()),
            ("PID", _ => new PidController()),
            ("PrivilegedFeedforward", _ => new PrivilegedFullStateController()),
        };

        [MenuItem("FluidStabilizer/Evaluate Recorded Drives")]
        public static void Run() => Evaluate(withShallowWater: false);

        [MenuItem("FluidStabilizer/Evaluate Recorded Drives (+ shallow water, slow)")]
        public static void RunWithShallowWater() => Evaluate(withShallowWater: true);

        private static void Evaluate(bool withShallowWater)
        {
            List<DriverTrace> traces = LoadTraces();
            if (traces.Count == 0)
            {
                Debug.LogWarning($"[trace-eval] no *.trace.csv in {TraceStore.Dir} — record some drives first (P in the play scene).");
                return;
            }

            List<(string, Func<ISloshModel>)> models = withShallowWater ? TraceEvaluator.BothSloshModels() : null;

            Debug.Log($"[trace-eval] {traces.Count} drive(s) × {Baselines.Length} controllers × {models?.Count ?? 1} model(s) …");

            List<TraceEvalRow> rows;
            using (var log = new EpisodeLog(EpisodeLog.DefaultPath, $"trace-eval {DateTime.Now:yyyy-MM-dd HH:mm}"))
                rows = TraceEvaluator.Run(traces, Baselines, models, log);
            string report = TraceEvaluator.FormatReport(rows);

            Directory.CreateDirectory("Logs");
            string output = RunProvenance.NewPath("Logs/trace-eval.txt");
            File.WriteAllText(output, report);
            Debug.Log($"[trace-eval] → {output}  (+ {EpisodeLog.DefaultPath})\n\n" + report);
        }

        private static List<DriverTrace> LoadTraces() => TraceStore.LoadDataset();
    }
}
