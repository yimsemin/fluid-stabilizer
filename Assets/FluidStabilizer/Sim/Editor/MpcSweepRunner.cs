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
using UnityEditor;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// Quick MPC option comparison on the first N seconds of selected train/validation
    /// drives, several option sets in one Unity session (MPC runs ~2.5 s CPU per
    /// simulated second, so full sweeps are slow).
    ///   -mpcVariants "A:Samples=24;Iterations=1|B:DisturbanceDecaySeconds=0.3"
    ///   -mpcSeconds 15  -mpcDrives 0,3,7   (indices into train+validation)
    /// </summary>
    public static class MpcSweepRunner
    {
        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string key, string d) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }

            double seconds = double.Parse(Arg("-mpcSeconds", "15"), CultureInfo.InvariantCulture);
            int[] idx = Arg("-mpcDrives", "1,3,7").Split(',').Select(int.Parse).ToArray();
            var all = new List<DriverTrace>();
            all.AddRange(TraceStore.LoadDataset("train"));
            all.AddRange(TraceStore.LoadDataset("validation"));
            var traces = idx.Select(i => all[i]).ToList();

            var sb = new StringBuilder();
            double rigidTotal = 0;
            var rigid = new Dictionary<string, double>();
            foreach (var t in traces)
            {
                double r = Run(t, new HoldController(), seconds, out _);
                rigid[t.Label] = r; rigidTotal += r;
            }
            sb.AppendLine($"drives {string.Join(",", idx)}  first {seconds:F0} s;  rigid total {rigidTotal:F2} L");

            foreach (string variant in Arg("-mpcVariants", "default:").Split('|'))
            {
                string[] nv = variant.Split(new[] { ':' }, 2);
                MpcOptions o = Parse(nv.Length > 1 ? nv[1] : "");
                double total = 0; long ticks = 0, locked = 0;
                var per = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var t in traces)
                {
                    var c = new OracleMpcController(t.ToConfig(), o);
                    double s = Run(t, c, seconds, out _);
                    total += s; ticks += c.Ticks; locked += c.LockedTicks;
                    per.Add($"{s:F1}/{rigid[t.Label]:F1}");
                }
                string line = $"  {nv[0],-22} {total,7:F2} L  {(total / rigidTotal - 1.0),7:P1} vs rigid   locked {(double)locked / Math.Max(1, ticks):P0}   [{string.Join(" ", per)}]  {sw.Elapsed.TotalSeconds:F0}s";
                sb.AppendLine(line);
                Debug.Log("[mpc-sweep] " + line);
            }
            Directory.CreateDirectory("Logs");
            string path = RunProvenance.NewPath("Logs/mpc-sweep.txt");
            File.WriteAllText(path, sb.ToString());
            Debug.Log("[mpc-sweep] → " + path + "\n" + sb);
        }

        private static double Run(DriverTrace t, IController c, double seconds, out EpisodeResult r)
        {
            SimulationConfig cfg = t.ToConfig();
            cfg.EpisodeSeconds = Math.Min(seconds, cfg.EpisodeSeconds);
            var loop = new SimulationLoop(cfg, c, new RecordedTraceDriver(t), t.MakeSloshModel());
            r = loop.RunEpisode(t.Seed);
            return r.TotalOverflowVolume * 1000.0;
        }

        private static MpcOptions Parse(string spec)
        {
            var o = new MpcOptions();
            foreach (string kv in spec.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = kv.Split('=');
                FieldInfo f = typeof(MpcOptions).GetField(p[0].Trim());
                if (f == null) throw new ArgumentException("Unknown MpcOptions field: " + p[0]);
                double v = double.Parse(p[1], CultureInfo.InvariantCulture);
                f.SetValue(o, f.FieldType == typeof(int) ? (object)(int)v : f.FieldType == typeof(ulong) ? (object)(ulong)v : v);
            }
            return o;
        }
    }
}
