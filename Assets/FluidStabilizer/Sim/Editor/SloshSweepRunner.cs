using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Sim;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// Passive-solution baseline: how much does spill drop when only the tank
    /// (damping, fill, size) changes, with no active control? Each variant overrides
    /// <see cref="SloshConfig"/> fields on every train+validation drive and reports
    /// total spill for the rigid hold ① and the specific-force rule ②.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.SloshSweepRunner.Run \
    ///     [-slVariants "base:|zeta2:DampingRatio=0.16|fill40:FillRatio=0.4"]
    /// Without -slVariants a built-in damping / fill / geometry grid runs.
    /// Pendulum model by default: a damping increase is a baffle *approximation* (F3).
    /// -slShallow scores under the shallow-water model instead (geometry variants only;
    /// damping fields are pendulum parameters).
    /// </summary>
    public static class SloshSweepRunner
    {
        private const string DefaultVariants =
            "base:" +
            "|zeta x2:DampingRatio=0.16|zeta x4:DampingRatio=0.32|zeta x8:DampingRatio=0.64" +
            "|quad x2:QuadraticDamping=1.8|quad x4:QuadraticDamping=3.6" +
            "|both x4:DampingRatio=0.32;QuadraticDamping=3.6" +
            "|fill 0.30:FillRatio=0.30|fill 0.40:FillRatio=0.40|fill 0.50:FillRatio=0.50|fill 0.62:FillRatio=0.62|fill 0.75:FillRatio=0.75" +
            "|rim 0.8:TankHeightZ=0.8" +
            "|rim 0.8 fill 0.5:TankHeightZ=0.8;FillRatio=0.5";

        private static bool _shallow;

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string key, string d) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }

            var traces = new List<DriverTrace>();
            traces.AddRange(TraceStore.LoadDataset("train"));
            traces.AddRange(TraceStore.LoadDataset("validation"));

            _shallow = args.Contains("-slShallow");
            var sb = new StringBuilder();
            sb.AppendLine($"drives {traces.Count} (train+validation), {(_shallow ? "shallow-water" : "pendulum")} model, spill in litres");
            sb.AppendLine($"{"variant",-20} {"rigid",8} {"vs base",8} {"SFA",8} {"vs base",8}  per-drive rigid");

            double baseRigid = 0, baseSfa = 0;
            foreach (string variant in Arg("-slVariants", DefaultVariants).Split('|'))
            {
                string[] nv = variant.Split(new[] { ':' }, 2);
                var overrides = Parse(nv.Length > 1 ? nv[1] : "");
                double rigid = 0, sfa = 0;
                var per = new List<string>();
                foreach (var t in traces)
                {
                    double r = Spill(t, new HoldController(), overrides);
                    rigid += r; per.Add(r.ToString("F1", CultureInfo.InvariantCulture));
                    sfa += Spill(t, new SpecificForceAlignmentController(), overrides);
                }
                if (nv[0] == "base") { baseRigid = rigid; baseSfa = sfa; }
                string line = $"{nv[0],-20} {rigid,8:F1} {(baseRigid > 0 ? rigid / baseRigid - 1 : 0),8:P0} {sfa,8:F1} {(baseSfa > 0 ? sfa / baseSfa - 1 : 0),8:P0}  [{string.Join(" ", per)}]";
                sb.AppendLine(line);
                Debug.Log("[slosh-sweep] " + line);
            }

            Directory.CreateDirectory("Logs");
            string path = RunProvenance.NewPath("Logs/slosh-sweep.txt");
            File.WriteAllText(path, sb.ToString());
            Debug.Log("[slosh-sweep] → " + path + "\n" + sb);
        }

        private static double Spill(DriverTrace t, IController c, Dictionary<FieldInfo, double> overrides)
        {
            SimulationConfig cfg = t.ToConfig();
            foreach (var kv in overrides) kv.Key.SetValue(cfg.Slosh, kv.Value);
            var loop = new SimulationLoop(cfg, c, new RecordedTraceDriver(t),
                _shallow ? new Core.Sim.SloshModels.ShallowWaterSloshModel() : t.MakeSloshModel());
            return loop.RunEpisode(t.Seed).TotalOverflowVolume * 1000.0;
        }

        private static Dictionary<FieldInfo, double> Parse(string spec)
        {
            var d = new Dictionary<FieldInfo, double>();
            foreach (string kv in spec.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = kv.Split('=');
                FieldInfo f = typeof(SloshConfig).GetField(p[0].Trim());
                if (f == null) throw new ArgumentException("Unknown SloshConfig field: " + p[0]);
                d[f] = double.Parse(p[1], CultureInfo.InvariantCulture);
            }
            return d;
        }
    }
}
