using System.Collections.Generic;
using System.IO;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Sim;
using UnityEditor;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// MPC step 0: where does the rigid baseline's spill come from? Runs the
    /// train + validation drives under Hold and writes Logs/spill-diagnostics-*.txt.
    ///   Unity -batchmode -quit -nographics -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.SpillDiagnosticsRunner.Run
    /// </summary>
    public static class SpillDiagnosticsRunner
    {
        private static readonly double[] Thresholds = { 3.0, 4.0, 6.0, 8.0 };

        [MenuItem("FluidStabilizer/MPC step 0 — spill diagnostics")]
        public static void Run()
        {
            var traces = new List<DriverTrace>();
            traces.AddRange(TraceStore.LoadDataset("train"));
            traces.AddRange(TraceStore.LoadDataset("validation"));

            var rows = new List<SpillBreakdown>();
            foreach (double thr in Thresholds)
                foreach (var t in traces)
                    rows.Add(SpillDiagnostics.Analyse(t, new HoldController(), t2 => t2.MakeSloshModel(), thr));

            Directory.CreateDirectory("Logs");
            string path = RunProvenance.NewPath("Logs/spill-diagnostics.txt");
            string report = SpillDiagnostics.Format(rows);
            File.WriteAllText(path, report);
            Debug.Log($"[spill-diagnostics] → {path}\n\n" + report);
        }
    }
}
