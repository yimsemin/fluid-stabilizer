using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Sim;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// Active control vs passive baffles: rigid ①, SpecificForceAlignment ② and the oracle
    /// MPC on the same drives, under tanks with and without baffles. The plant is the
    /// shallow-water model (the pendulum ignores baffles); the MPC's internal model is
    /// still the pendulum with the un-baffled parameters, so it does not know about
    /// the baffles. Drives run in parallel (each loop owns all its state).
    ///
    ///   Unity -batchmode -quit -nographics -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.ActiveVsBaffleRunner.Run \
    ///     [-abVariants "none:|X2Y1 K20:BaffleCountX=2;BaffleCountY=1;BaffleLossCoeff=20"]
    /// A variant named "pend ..." uses the pendulum plant (cross-check against F14).
    /// </summary>
    public static class ActiveVsBaffleRunner
    {
        private const string DefaultVariants =
            "pend none:" +
            "|none:" +
            "|X2Y1 K20:BaffleCountX=2;BaffleCountY=1;BaffleLossCoeff=20" +
            "|X3Y2 K50:BaffleCountX=3;BaffleCountY=2;BaffleLossCoeff=50";

        private static readonly string[] Controllers = { "Rigid", "SpecificForce", "OracleMPC" };

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            int ai = Array.IndexOf(args, "-abVariants");
            string spec = ai >= 0 && ai + 1 < args.Length ? args[ai + 1] : DefaultVariants;

            var traces = new List<DriverTrace>();
            traces.AddRange(TraceStore.LoadDataset("train"));
            traces.AddRange(TraceStore.LoadDataset("validation"));
            var variants = spec.Split('|').Select(v => v.Split(new[] { ':' }, 2)).ToList();

            var jobs = new List<(int v, int t, int c)>();
            for (int v = 0; v < variants.Count; v++)
                for (int t = 0; t < traces.Count; t++)
                    for (int c = 0; c < Controllers.Length; c++) jobs.Add((v, t, c));

            var spill = new double[variants.Count, traces.Count, Controllers.Length];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var opt = new MpcOptions();
            Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, job =>
            {
                string name = variants[job.v][0];
                bool pend = name.StartsWith("pend");
                DriverTrace tr = traces[job.t];
                SimulationConfig cfg = tr.ToConfig();
                Apply(cfg.Slosh, variants[job.v].Length > 1 ? variants[job.v][1] : "");
                IController ctrl = job.c == 0 ? new HoldController()
                    : job.c == 1 ? (IController)new SpecificForceAlignmentController()
                    : new OracleMpcController(cfg, opt);
                ISloshModel plant = pend ? new PendulumSloshModel() : (ISloshModel)new ShallowWaterSloshModel();
                var loop = new SimulationLoop(cfg, ctrl, new RecordedTraceDriver(tr), plant);
                spill[job.v, job.t, job.c] = loop.RunEpisode(tr.Seed).TotalOverflowVolume * 1000.0;
            });

            var sb = new StringBuilder();
            sb.AppendLine($"active control vs baffles — {traces.Count} drives (train+validation), spill in litres, wall {sw.Elapsed.TotalSeconds:F0} s");
            sb.AppendLine("plant: shallow water unless the variant starts with 'pend'; MPC internal model: pendulum, no baffles");
            double baseRigid = 0;
            for (int v = 0; v < variants.Count; v++) if (variants[v][0] == "none") baseRigid = Sum(spill, v, 0, traces.Count);
            sb.AppendLine($"{"variant",-16} {"controller",-14} {"total",8} {"vs rigid (same tank)",21} {"vs rigid (no baffle)",21}");
            for (int v = 0; v < variants.Count; v++)
            {
                double rigid = Sum(spill, v, 0, traces.Count);
                for (int c = 0; c < Controllers.Length; c++)
                {
                    double s = Sum(spill, v, c, traces.Count);
                    string vsBase = baseRigid > 0 && !variants[v][0].StartsWith("pend") ? $"{s / baseRigid - 1:P1}" : "—";
                    var per = string.Join(" ", Enumerable.Range(0, traces.Count).Select(t => spill[v, t, c].ToString("F1", CultureInfo.InvariantCulture)));
                    sb.AppendLine($"{variants[v][0],-16} {Controllers[c],-14} {s,8:F1} {s / rigid - 1,21:P1} {vsBase,21}  [{per}]");
                }
            }
            Directory.CreateDirectory("Logs");
            string path = RunProvenance.NewPath("Logs/active-vs-baffle.txt");
            File.WriteAllText(path, sb.ToString());
            Debug.Log("[active-vs-baffle] → " + path + "\n" + sb);
        }

        private static double Sum(double[,,] a, int v, int c, int nT)
        {
            double s = 0;
            for (int t = 0; t < nT; t++) s += a[v, t, c];
            return s;
        }

        private static void Apply(SloshConfig cfg, string spec)
        {
            foreach (string kv in spec.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = kv.Split('=');
                FieldInfo f = typeof(SloshConfig).GetField(p[0].Trim());
                if (f == null) throw new ArgumentException("Unknown SloshConfig field: " + p[0]);
                double v = double.Parse(p[1], CultureInfo.InvariantCulture);
                f.SetValue(cfg, f.FieldType == typeof(int) ? (object)(int)v : v);
            }
        }
    }
}
