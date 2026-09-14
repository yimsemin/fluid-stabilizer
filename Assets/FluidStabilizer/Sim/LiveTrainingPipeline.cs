using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Rl;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// The live-demo training flow (default path only — knots/restarts/models,
    /// see <see cref="HindsightOptions"/> defaults): every recorded drive
    /// (road on, fill ≥ 0.60 — same filter as <c>FluidStabilizer ▸ Feasibility
    /// Ceiling</c>) → hindsight ceiling → BC dataset → <c>bc_pretrain.py</c>.
    ///
    /// This is deliberately a smaller, fixed-default sibling of the
    /// <c>HindsightEvalRunner</c> / <c>BcExportRunner</c> menu items (which carry
    /// the full CLI-flag surface for the sweep/gate science work) — the demo
    /// panel doesn't need those flags and re-running the real gate scripts is
    /// the source of truth for findings, not this. It stays in the non-Editor
    /// <c>Sim</c> assembly (not <c>Sim/Editor</c>) purely so it can run its
    /// search on a background thread without touching UnityEditor APIs; like
    /// the policy-server buttons in <see cref="ControlPanel"/> it only actually
    /// runs inside the Editor (spawns <c>python</c> the same way).
    ///
    /// Runs one hindsight search + one python subprocess at a time on a
    /// background <see cref="Thread"/> (both are pure CPU / IPC, no Unity API),
    /// polled by <see cref="LiveTrainingPanel"/> via <see cref="Snapshot"/>.
    /// </summary>
    public sealed class LiveTrainingPipeline
    {
        public enum Stage { Idle, Hindsight, BcExport, BcTrain, Done, Failed }

        public struct Snapshot
        {
            public Stage Stage;
            public float Progress;      // 0..1 overall
            public string Status;       // one-line "what's happening now"
            public string Error;        // set only when Stage == Failed
            public string CheckpointPath; // set only when Stage == Done
            public int TraceCount;
        }

        // Weights sum to 1 — hindsight search dominates wall time (hundreds of
        // simulated episodes per drive), BC export is file I/O, BC train is a
        // few dozen fast epochs on a small dataset.
        private const float HindsightWeight = 0.72f, BcExportWeight = 0.05f, BcTrainWeight = 0.23f;
        private const int BcEpochs = 150; // bc_pretrain.py's own default — passed explicitly so we know the total to parse progress against.

        private readonly object _lock = new object();
        private Snapshot _snap = new Snapshot { Stage = Stage.Idle };
        private volatile bool _running;

        public bool Running => _running;
        public Snapshot GetSnapshot() { lock (_lock) return _snap; }

        private void Set(Stage stage, float progress, string status, string error = null, string checkpoint = null, int traceCount = -1)
        {
            lock (_lock)
            {
                _snap.Stage = stage;
                _snap.Progress = Mathf.Clamp01(progress);
                _snap.Status = status;
                if (error != null) _snap.Error = error;
                if (checkpoint != null) _snap.CheckpointPath = checkpoint;
                if (traceCount >= 0) _snap.TraceCount = traceCount;
            }
        }

        /// <summary>Drives usable for training right now (same filter the hindsight gate uses).</summary>
        public static List<DriverTrace> UsableTraces() =>
            TraceStore.LoadDataset("train").Where(t => t.RoadEnabled && t.FillRatio >= 0.60 - 1e-9).ToList();

        public void Start()
        {
            if (_running) return;
            List<DriverTrace> traces = UsableTraces(); // file I/O — do it on the caller's (main) thread
            if (traces.Count == 0)
            {
                Set(Stage.Failed, 0f, "", "학습에 쓸 주행이 없음 — 도로 켜짐 + fill ≥ 0.60 주행을 먼저 녹화 (P)", traceCount: 0);
                return;
            }
            _running = true;
            Set(Stage.Hindsight, 0f, $"② 천장 탐색 준비 중… ({traces.Count}개 주행)", traceCount: traces.Count);
            var thread = new Thread(() => RunWorker(traces)) { IsBackground = true, Name = "LiveTraining" };
            thread.Start();
        }

        private void RunWorker(List<DriverTrace> traces)
        {
            try
            {
                var opts = new HindsightOptions(); // defaults == Feasibility Ceiling menu with no flags
                int perTrace = HindsightOptimizer.EstimatedEvalCount(opts);

                string outDir = RunProvenance.NewPath("Logs/hindsight-live");
                Directory.CreateDirectory(outDir);
                var reports = new List<HindsightDriveReport>();

                for (int i = 0; i < traces.Count; i++)
                {
                    DriverTrace trace = traces[i];
                    int ti = i;
                    HindsightDriveReport r = HindsightOptimizer.Run(trace, opts, evalsDone =>
                    {
                        float frac = (ti + (float)evalsDone / perTrace) / traces.Count;
                        Set(Stage.Hindsight, frac * HindsightWeight,
                            $"② 천장 탐색 중… 주행 {ti + 1}/{traces.Count}  ({evalsDone}/{perTrace})");
                    });
                    reports.Add(r);
                    using (var w = new StreamWriter(Path.Combine(outDir, r.Trace + ".traj.csv"), false))
                        HindsightOptimizer.WriteTrajectoryCsv(w, r);
                }
                File.WriteAllText(Path.Combine(outDir, "report.txt"), HindsightOptimizer.FormatReport(reports, opts));

                Set(Stage.BcExport, HindsightWeight, "③ 학습 데이터셋 만드는 중…");
                string datasetPath = ExportBcDataset(traces, outDir);
                if (datasetPath == null)
                {
                    Set(Stage.Failed, HindsightWeight, "", "BC 데이터셋 생성 실패 — 궤적에서 유효한 틱을 못 뽑음");
                    return;
                }

                Set(Stage.BcTrain, HindsightWeight + BcExportWeight, "④ 정책 학습 시작…");
                string runDir = Path.Combine(RepoRoot(), "python", "runs", "live-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(runDir);
                string checkpointPath = Path.Combine(runDir, "actor.pt");
                RunBcTrain(datasetPath, checkpointPath, frac =>
                    Set(Stage.BcTrain, HindsightWeight + BcExportWeight + frac * BcTrainWeight,
                        $"④ 정책 학습 중… {frac * 100:F0}%"));

                Set(Stage.Done, 1f, "완료 — 컨트롤 패널에서 서버를 (재)시작하면 반영됨", checkpoint: checkpointPath);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Set(Stage.Failed, 0f, "", "오류: " + e.Message);
            }
            finally
            {
                _running = false;
            }
        }

        // ---------------------------------------------------------- BC export
        // Mirrors BcExportRunner's default path (no residual/lock-capable/legacy
        // flags — the demo trains a plain policy on trajectories generated in
        // this same run, so there's nothing stale to validate against).

        private static string ExportBcDataset(List<DriverTrace> traces, string trajDir)
        {
            var all = new List<BcDatasetExporter.Sample>();
            var used = new List<string>();
            double scale = 0;
            int obsSize = 0;
            const int stack = 6;

            foreach (DriverTrace trace in traces)
            {
                string trajPath = Path.Combine(trajDir, trace.Label + ".traj.csv");
                if (!File.Exists(trajPath)) continue;

                List<BcDatasetExporter.TrajRow> traj;
                using (var r = new StreamReader(trajPath)) traj = BcDatasetExporter.LoadTrajectory(r);
                if (traj.Count == 0) continue;

                var samples = BcDatasetExporter.FromDrive(trace, traj, trace.MakeSloshModel, stack);
                if (samples.Count == 0) continue;

                all.AddRange(samples);
                used.Add(trace.Label);
                scale = trace.ToConfig().Gimbal.MaxRateRadPerSec;
                obsSize = samples[0].Obs.Length;
            }

            if (all.Count == 0) return null;

            Directory.CreateDirectory("Logs/bc");
            string output = RunProvenance.NewPath("Logs/bc/live-dataset.csv");
            using (var w = new StreamWriter(output, false))
                BcDatasetExporter.Write(w, all, obsSize, stack, scale, used,
                    BcDatasetExporter.LastContract, BcDatasetExporter.LastClampedFraction);
            return output;
        }

        // ------------------------------------------------------------ BC train

        private static void RunBcTrain(string datasetPath, string outPath, Action<float> onProgress)
        {
            string script = Path.Combine(RepoRoot(), "python", "bc_pretrain.py");
            if (!File.Exists(script)) throw new FileNotFoundException("bc_pretrain.py 없음", script);

            var psi = new ProcessStartInfo(PythonExe())
            {
                // -u: unbuffered stdout, so epoch progress lines arrive as they're printed.
                Arguments = $"-u \"{script}\" --dataset \"{datasetPath}\" --out \"{outPath}\" --epochs {BcEpochs}",
                WorkingDirectory = Path.Combine(RepoRoot(), "python"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var epochRe = new Regex(@"^epoch\s+(\d+)");
            var stderr = new StringBuilder();
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Match m = epochRe.Match(e.Data);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int ep))
                    onProgress(Mathf.Clamp01((float)ep / BcEpochs));
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();

            if (proc.ExitCode != 0 || !File.Exists(outPath))
                throw new Exception($"bc_pretrain.py 실패 (code {proc.ExitCode}): {stderr}");
        }

        // ------------------------------------------------------------- python

        /// <summary>Reused by <see cref="LiveTrainingPanel"/> to launch the "새 정책" comparison server.</summary>
        internal static string RepoRoot() =>
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        internal static string PythonExe()
        {
            string venv = Path.Combine(RepoRoot(), "python", ".venv", "bin", "python");
            if (File.Exists(venv)) return venv;
            venv = Path.Combine(RepoRoot(), "python", ".venv", "Scripts", "python.exe");
            if (File.Exists(venv)) return venv;
            return Application.platform == RuntimePlatform.WindowsEditor
                || Application.platform == RuntimePlatform.WindowsPlayer ? "python" : "python3";
        }
    }
}
