using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Rl;
using FluidStabilizer.Sim;
using UnityEditor;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// experiment-plan step C1 — build the behaviour-cloning dataset. Pairs each
    /// <c>Traces/*.trace.csv</c> with its <c>Logs/hindsight/&lt;label&gt;.traj.csv</c>
    /// (run the Feasibility Ceiling first) and writes
    /// <c>Logs/bc/dataset.csv</c> — one row per control tick,
    /// <c>obs… , aPitch, aRoll</c>.
    ///
    ///   Menu ▸ FluidStabilizer ▸ Export BC Dataset
    ///   -executeMethod FluidStabilizer.SimEditor.BcExportRunner.Run  [-bcStack 6] [-bcSlosh pendulum]
    /// </summary>
    public static class BcExportRunner
    {
        [MenuItem("FluidStabilizer/Export BC Dataset")]
        public static void RunMenu() => Execute();

        public static void Run() => Execute();

        private static void Execute()
        {
            string[] a = Environment.GetCommandLineArgs();
            int stack = ArgInt(a, "-bcStack", 6);
            string sloshName = ArgStr(a, "-bcSlosh", "pendulum");
            Func<FluidStabilizer.Contracts.ISloshModel> slosh = sloshName == "shallow"
                ? () => new ShallowWaterSloshModel()
                : (Func<FluidStabilizer.Contracts.ISloshModel>)(() => new PendulumSloshModel());

            const string trajDir = "Logs/hindsight";
            if (!Directory.Exists(trajDir))
            {
                Debug.LogWarning($"[bc] no {trajDir} — run FluidStabilizer ▸ Feasibility Ceiling (Hindsight) first.");
                return;
            }
            if (!Directory.Exists(TraceStore.Dir))
            {
                Debug.LogWarning($"[bc] no {TraceStore.Dir}.");
                return;
            }

            var all = new List<BcDatasetExporter.Sample>();
            var used = new List<string>();
            double scale = 0;
            int obsSize = 0;

            foreach (string tracePath in Directory.GetFiles(TraceStore.Dir, "*" + TraceStore.Extension).OrderBy(p => p))
            {
                DriverTrace trace = DriverTrace.LoadFromFile(tracePath);
                string trajPath = Path.Combine(trajDir, trace.Label + ".traj.csv");
                if (!File.Exists(trajPath)) continue;

                List<BcDatasetExporter.TrajRow> traj;
                using (var r = new StreamReader(trajPath)) traj = BcDatasetExporter.LoadTrajectory(r);
                if (traj.Count == 0) continue;

                var samples = BcDatasetExporter.FromDrive(trace, traj, slosh, stack);
                if (samples.Count == 0) continue;

                all.AddRange(samples);
                used.Add(trace.Label);
                scale = trace.ToConfig().Gimbal.MaxRateRadPerSec;
                obsSize = samples[0].Obs.Length;
                Debug.Log($"[bc] {trace.Label}: {samples.Count} ticks");
            }

            if (all.Count == 0)
            {
                Debug.LogWarning("[bc] no trace had a matching Logs/hindsight/<label>.traj.csv — nothing exported.");
                return;
            }

            Directory.CreateDirectory("Logs/bc");
            using (var w = new StreamWriter("Logs/bc/dataset.csv", false))
                BcDatasetExporter.Write(w, all, obsSize, stack, scale, used);

            Debug.Log($"[bc] {all.Count} rows from {used.Count} drive(s) → Logs/bc/dataset.csv");
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
