using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
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
        private static readonly (string, Func<IController>)[] Baselines =
        {
            ("Rigid (Hold)", () => new HoldController()),
            ("SpecificForceAlignment", () => new SpecificForceAlignmentController()),
            ("PID", () => new PidController()),
            ("PrivilegedFeedforward", () => new PrivilegedFullStateController()),
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

            var models = new List<(string, Func<ISloshModel>)> { ("pendulum", () => new PendulumSloshModel()) };
            if (withShallowWater) models.Add(("shallow", () => new ShallowWaterSloshModel()));

            Debug.Log($"[trace-eval] {traces.Count} drive(s) × {Baselines.Length} controllers × {models.Count} model(s) …");

            var rows = TraceEvaluator.Run(traces, Baselines, models);
            string report = TraceEvaluator.FormatReport(rows);

            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/trace-eval.txt", report);
            Debug.Log("[trace-eval] → Logs/trace-eval.txt\n\n" + report);
        }

        private static List<DriverTrace> LoadTraces()
        {
            if (!Directory.Exists(TraceStore.Dir)) return new List<DriverTrace>();
            return Directory.GetFiles(TraceStore.Dir, "*" + TraceStore.Extension)
                .OrderBy(p => p)
                .Select(DriverTrace.LoadFromFile)
                .ToList();
        }
    }
}
