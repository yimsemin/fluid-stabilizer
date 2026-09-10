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
            // Targets are expressed in the contract the student will be trained and
            // served in — a residual policy needs residual targets (PolicyContract).
            string bcBaseline = ArgStr(a, "-bcBaseline", null);
            double bcResidualScale = ArgDouble(a, "-bcResidualScale", double.NaN);
            bool bcLockCapable = ArgInt(a, "-bcLockCapable", 0) != 0;
            string sloshName = ArgStr(a, "-bcSlosh", null);
            Func<FluidStabilizer.Contracts.ISloshModel> slosh = sloshName == "shallow"
                ? () => new ShallowWaterSloshModel()
                : (Func<FluidStabilizer.Contracts.ISloshModel>)(() => new PendulumSloshModel());

            string trajDir = ArgStr(a, "-bcTrajDir", "Logs/hindsight");
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
            var evidence = new System.Text.StringBuilder();
            double scale = 0;
            int obsSize = 0;

            foreach (DriverTrace trace in TraceStore.LoadDataset("train"))
            {
                string trajPath = Path.Combine(trajDir, trace.Label + ".traj.csv");
                if (!File.Exists(trajPath)) throw new FileNotFoundException("Missing trajectory for assigned training drive", trajPath);

                var headers = File.ReadLines(trajPath).Where(l => l.StartsWith("# ") && l.Contains(":"))
                    .Select(l => l.Substring(2).Split(new[] { ':' }, 2)).ToDictionary(v => v[0].Trim(), v => v[1].Trim());
                string model = sloshName ?? trace.SloshModel;
                if (headers.TryGetValue("configSha256", out string configHash))
                {
                    if (configHash != FluidStabilizer.Core.Sim.ConfigSnapshot.Hash(FluidStabilizer.Core.Sim.ConfigSnapshot.Encode(trace.ToConfig()))
                        || !headers.TryGetValue("inputSha256", out string inputHash) || inputHash != trace.InputSha256
                        || !headers.TryGetValue("optimizeModel", out string optimizeModel) || optimizeModel != model)
                        throw new InvalidDataException("Trajectory config/input/model mismatch: " + trajPath);
                    if (headers.TryGetValue("lockCapable", out string locked) && locked != "False")
                        throw new InvalidDataException("BC absolute-rate export does not reproduce lock-capable trajectories: " + trajPath);
                }
                else if (ArgInt(a, "-bcAllowLegacyTrajectory", 0) != 1)
                    throw new InvalidDataException("Legacy trajectory settings unknown; explicitly pass -bcAllowLegacyTrajectory 1 for exploratory use only.");
                evidence.AppendLine(FluidStabilizer.Core.Sim.Eval.RunProvenance.Describe(trace, trace.ToConfig(), model));
                evidence.AppendLine("trajectory=" + trajPath + ";sha256=" + FluidStabilizer.Core.Sim.ConfigSnapshot.FileHash(trajPath)
                    + ";legacy-assumption=" + !headers.ContainsKey("configSha256"));
                List<BcDatasetExporter.TrajRow> traj;
                using (var r = new StreamReader(trajPath)) traj = BcDatasetExporter.LoadTrajectory(r);
                if (traj.Count == 0) continue;

                var samples = BcDatasetExporter.FromDrive(
                    trace, traj, sloshName == null ? trace.MakeSloshModel : slosh, stack,
                    baselineId: bcBaseline, residualScaleRadPerSec: bcResidualScale,
                    lockCapable: bcLockCapable);
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
            string output = FluidStabilizer.Core.Sim.Eval.RunProvenance.NewPath("Logs/bc/dataset.csv");
            using (var w = new StreamWriter(output, false))
                BcDatasetExporter.Write(w, all, obsSize, stack, scale, used,
                    BcDatasetExporter.LastContract, BcDatasetExporter.LastClampedFraction);

            File.WriteAllText(output + ".metadata.txt", evidence.ToString() + "\nstack=" + stack);
            Debug.Log($"[bc] {all.Count} rows from {used.Count} drive(s) → {output}");
        }

        private static double ArgDouble(string[] a, string key, double def) =>
            double.TryParse(ArgStr(a, key, null), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;

        private static string ArgStr(string[] a, string key, string def)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        private static int ArgInt(string[] a, string key, int def) =>
            int.TryParse(ArgStr(a, key, null), out int v) ? v : def;
    }
}
