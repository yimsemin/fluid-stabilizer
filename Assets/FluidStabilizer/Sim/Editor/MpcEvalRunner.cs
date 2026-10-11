using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// MPC evaluation (experiment-plan.md "다음 작업"): train + validation drives
    /// through rigid ①, SpecificForceAlignment ②, and the MPC, per drive and in total.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.MpcEvalRunner.Run \
    ///     [-mpcOpt "Samples=32;NoiseRad=0.08"] [-mpcShallow]
    ///
    /// -mpcOpt overrides any public <see cref="MpcOptions"/> field (numbers only).
    /// -mpcShallow also scores under the shallow-water model (step 3; slow).
    /// </summary>
    public static class MpcEvalRunner
    {
        [MenuItem("FluidStabilizer/Evaluate MPC")]
        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

            MpcOptions opt = Parse(Arg("-mpcOpt"));
            var traces = new List<DriverTrace>();
            traces.AddRange(TraceStore.LoadDataset("train"));
            traces.AddRange(TraceStore.LoadDataset("validation"));

            var controllers = new List<(string, Func<SimulationConfig, IController>)>
            {
                ("Rigid (Hold)", _ => new HoldController()),
                ("SpecificForceAlignment", _ => new SpecificForceAlignmentController()),
                ("OracleMPC", cfg => new OracleMpcController(cfg, opt)),
            };

            List<(string, Func<ISloshModel>)> models = args.Contains("-mpcShallow") ? TraceEvaluator.BothSloshModels() : null;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            List<TraceEvalRow> rows = TraceEvaluator.Run(traces, controllers, models);
            string report = TraceEvaluator.FormatReport(rows) + Totals(rows) +
                            $"\nMPC options: {Describe(opt)}\nwall time {sw.Elapsed.TotalSeconds:F0} s\n";

            Directory.CreateDirectory("Logs");
            string path = RunProvenance.NewPath("Logs/mpc-eval.txt");
            File.WriteAllText(path, report);
            Debug.Log($"[mpc-eval] → {path}\n\n" + report);
        }

        private static string Totals(List<TraceEvalRow> rows)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var byModel in rows.GroupBy(r => r.SloshModel))
            {
                sb.AppendLine($"### totals — slosh model {byModel.Key}");
                double rigid = byModel.Where(r => r.Controller.StartsWith("Rigid")).Sum(r => r.SpillLitres);
                foreach (var byCtrl in byModel.GroupBy(r => r.Controller))
                {
                    double sum = byCtrl.Sum(r => r.SpillLitres);
                    sb.AppendLine($"  {byCtrl.Key,-28} {sum,9:F2} L   {(rigid > 0 ? (sum / rigid - 1.0) : 0):P1} vs rigid");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static MpcOptions Parse(string spec)
        {
            var o = new MpcOptions();
            if (string.IsNullOrWhiteSpace(spec)) return o;
            foreach (string kv in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = kv.Split('=');
                FieldInfo f = typeof(MpcOptions).GetField(p[0].Trim());
                if (f == null) throw new ArgumentException("Unknown MpcOptions field: " + p[0]);
                double v = double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
                f.SetValue(o, f.FieldType == typeof(int) ? (object)(int)v : f.FieldType == typeof(ulong) ? (object)(ulong)v : v);
            }
            return o;
        }

        private static string Describe(MpcOptions o) =>
            string.Join("; ", typeof(MpcOptions).GetFields().Select(f => f.Name + "=" + f.GetValue(o)));
    }
}
