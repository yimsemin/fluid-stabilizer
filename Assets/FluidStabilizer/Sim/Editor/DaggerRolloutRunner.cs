using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Rl;
using FluidStabilizer.Sim;
using UnityEditor;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// DAgger step (experiment-plan A, findings F11). Runs the <b>learner</b> — the
    /// served policy — through each training drive and relabels every state it
    /// visits with what the hindsight teacher would have commanded at that time.
    /// Behaviour cloning only ever saw states on the teacher's own path; the learner
    /// drifts off it, so the states that matter were never in the data.
    ///
    ///   -executeMethod FluidStabilizer.SimEditor.DaggerRolloutRunner.Run
    ///       -traceManifest datasets-f8-multi.tsv -ppPort 5601 -daOut Logs/dagger/it1.csv
    ///       [-daBaseline hold] [-daResidualScale 3.5]
    ///
    /// The teacher is open-loop in time (a trajectory, not a feedback law), so the
    /// label at time t is the teacher's rate at t expressed as a residual on the
    /// baseline, exactly as <see cref="BcDatasetExporter.FromDrive"/> does. An idle
    /// teacher (all-zero trajectory) labels "stay locked". The output is a normal
    /// BC dataset; <c>python/dagger.py</c> aggregates and retrains.
    /// </summary>
    public static class DaggerRolloutRunner
    {
        [MenuItem("FluidStabilizer/DAgger Rollout (needs serve_policy.py)")]
        public static void RunMenu() => Execute();

        public static void Run() => Execute();

        private static void Execute()
        {
            string[] a = Environment.GetCommandLineArgs();
            string host = ArgStr(a, "-ppHost", "127.0.0.1");
            int port = ArgInt(a, "-ppPort", 5601);
            int stack = ArgInt(a, "-ppStack", 6);
            string baselineId = ArgStr(a, "-daBaseline", "hold");
            double residualScale = ArgDouble(a, "-daResidualScale", 3.5);
            string outPath = ArgStr(a, "-daOut", null);
            if (string.IsNullOrEmpty(outPath)) throw new ArgumentException("-daOut is required");
            if (File.Exists(outPath)) throw new IOException("Refusing to overwrite " + outPath);

            var samples = new List<BcDatasetExporter.Sample>();
            var used = new List<string>();
            PolicyContract contract = default;
            double scale = 0;
            int obsSize = 0;
            var summary = new System.Text.StringBuilder();

            foreach (DriverTrace trace in TraceStore.LoadDataset("train"))
            {
                string beside = trace.SourcePath.Substring(0, trace.SourcePath.Length - ".trace.csv".Length) + ".traj.csv";
                string trajPath = File.Exists(beside) ? beside : Path.Combine("Logs/hindsight", trace.Label + ".traj.csv");
                if (!File.Exists(trajPath)) throw new FileNotFoundException("Missing trajectory", trajPath);
                List<BcDatasetExporter.TrajRow> traj;
                using (var r = new StreamReader(trajPath)) traj = BcDatasetExporter.LoadTrajectory(r);
                if (traj.Count == 0) continue;
                bool idle = traj.All(t => t.PitchRate == 0.0 && t.RollRate == 0.0);

                SimulationConfig cfg = trace.ToConfig();
                var ctrl = new RemotePolicyController(host, port, stack, cfg.Gimbal.MaxRateRadPerSec,
                    log: s => Debug.Log(s), gimbal: cfg.Gimbal);
                IController baseline = PolicyContract.MakeBaseline(baselineId);
                baseline.Reset();

                int tick = 0;
                var drive = new List<BcDatasetExporter.Sample>();
                ctrl.ObservationTap = (obs, sensors) =>
                {
                    BcDatasetExporter.TrajRow row = traj[Math.Min(tick++, traj.Count - 1)];
                    StabilizerCommand b = baseline.ComputeCommand(sensors);
                    ActionComposer.BaselineRates(b, sensors, out double bp, out double br);
                    double rawP = (row.PitchRate - bp) / residualScale;
                    double rawR = (row.RollRate - br) / residualScale;
                    drive.Add(new BcDatasetExporter.Sample
                    {
                        Obs = (float[])obs.Clone(),
                        ActPitch = (float)Math.Max(-1.0, Math.Min(1.0, rawP)),
                        ActRoll = (float)Math.Max(-1.0, Math.Min(1.0, rawR)),
                        ActLock = idle ? 0f : 1f,
                    });
                };

                var loop = new SimulationLoop(cfg, ctrl, new RecordedTraceDriver(trace), trace.MakeSloshModel());
                EpisodeResult res = loop.RunEpisode(trace.Seed);
                string status = ctrl.ExecutionStatus;
                contract = ctrl.Contract;
                ctrl.Dispose();
                if (status != "policy-ok")
                    throw new InvalidOperationException($"learner did not run on {trace.Label}: {status} ({ctrl.FallbackReason})");

                samples.AddRange(drive);
                used.Add(trace.Label);
                scale = cfg.Gimbal.MaxRateRadPerSec;
                obsSize = drive[0].Obs.Length;
                summary.AppendLine($"{trace.Label}\t{(idle ? "idle" : "active")}\tspill={(res.TotalOverflowVolume * 1000.0):F2}\tticks={drive.Count}");
                Debug.Log($"[dagger] {trace.Label} {(idle ? "idle" : "active")} learner spill {(res.TotalOverflowVolume * 1000.0):F2} L, {drive.Count} states");
            }

            if (samples.Count == 0) throw new InvalidOperationException("no states collected");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            using (var w = new StreamWriter(outPath, false))
                BcDatasetExporter.Write(w, samples, obsSize, stack, scale, used, contract, 0.0);
            File.WriteAllText(outPath + ".summary.txt", summary.ToString());
            Debug.Log($"[dagger] {samples.Count} relabelled states from {used.Count} drive(s) → {outPath}");
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
