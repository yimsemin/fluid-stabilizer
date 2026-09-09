using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Core.Sim.Eval
{
    public struct TraceEvalRow
    {
        public string Trace;
        public string Evidence;
        public string ExecutionStatus;
        public bool ValidComparison => ExecutionStatus == "analytic" || ExecutionStatus == "policy-ok";
        public string Controller;
        public string SloshModel;
        public double DurationSeconds;
        public double SpillLitres;
        public double WorstFreeboard;      // m (negative = overflowed)
        public double SloshEnergyIntegral; // J·s
        public double MeanEffort;
        public double TorqueSatFraction;
    }

    /// <summary>
    /// The headline three-way comparison (experiment-plan.md step 2c): replay each
    /// recorded human drive through each controller, under each slosh model, and
    /// report per drive — litres spilled first. Unlike <see cref="Evaluator"/>
    /// (synthetic scenarios × seeds), the unit here is one real recorded drive,
    /// which is what the owner's headline result is measured on.
    /// </summary>
    public static class TraceEvaluator
    {
        public static List<(string name, Func<ISloshModel> make)> BothSloshModels() =>
            new List<(string, Func<ISloshModel>)>
            {
                ("pendulum", () => new PendulumSloshModel()),
                ("shallow", () => new ShallowWaterSloshModel()),
            };

        public static List<TraceEvalRow> Run(
            IReadOnlyList<DriverTrace> traces,
            IReadOnlyList<(string name, Func<IController> make)> controllers,
            IReadOnlyList<(string name, Func<ISloshModel> make)> sloshModels,
            EpisodeLog log = null)
        {
            TraceDataset.ValidateUnique(traces);
            var rows = new List<TraceEvalRow>();

            foreach (var trace in traces)
            foreach (var (sloshName, makeSlosh) in sloshModels ?? new[] { (trace.SloshModel, (Func<ISloshModel>)trace.MakeSloshModel) })
            foreach (var (ctrlName, makeCtrl) in controllers)
            {
                IController ctrl = makeCtrl();
                SimulationConfig cfg = trace.ToConfig();
                var loop = new SimulationLoop(
                    cfg, ctrl, new RecordedTraceDriver(trace), makeSlosh());
                EpisodeResult r = loop.RunEpisode(trace.Seed);
                log?.Add("trace-eval", cfg, r, trace, ctrl);

                rows.Add(new TraceEvalRow
                {
                    Trace = trace.Label,
                    Evidence = RunProvenance.Describe(trace, cfg, sloshName, ctrl),
                    ExecutionStatus = (ctrl as IPolicyExecutionStatus)?.ExecutionStatus ?? "analytic",
                    Controller = ctrlName,
                    SloshModel = sloshName,
                    DurationSeconds = r.DurationSeconds,
                    SpillLitres = r.TotalOverflowVolume * 1000.0,
                    WorstFreeboard = r.WorstFreeboard,
                    SloshEnergyIntegral = r.SloshEnergyIntegral,
                    MeanEffort = r.MeanAbsActionRate,
                    TorqueSatFraction = r.TorqueSaturationFraction,
                });
                if (ctrl is IDisposable d) d.Dispose();
            }

            return rows;
        }

        public static string FormatReport(IReadOnlyList<TraceEvalRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("fluid-stabilizer — recorded-drive evaluation");
            sb.AppendLine($"generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine();

            foreach (var byTrace in rows.GroupBy(r => r.Trace))
            {
                foreach (var row in byTrace) sb.AppendLine(row.Evidence);
                double dur = byTrace.First().DurationSeconds;
                sb.AppendLine($"### {byTrace.Key}   ({dur:F1} s)");

                foreach (var byModel in byTrace.GroupBy(r => r.SloshModel))
                {
                    sb.AppendLine($"  -- slosh model: {byModel.Key} --");
                    sb.AppendLine("  controller                     spill[L]   worstFB[m]   sloshE∫   effort   torqueSat");
                    sb.AppendLine("  " + new string('-', 96));
                    foreach (var r in byModel.OrderBy(r => r.SpillLitres))
                        sb.AppendLine(string.Format(
                            "  {0,-28}  {1,8:F3}  {2,10:F4}  {3,8:F2}  {4,7:F3}  {5,8:P0}",
                            Trim(r.Controller, 28) + " [" + r.ExecutionStatus + "]", r.SpillLitres, r.WorstFreeboard,
                            r.SloshEnergyIntegral, r.MeanEffort, r.TorqueSatFraction));
                    sb.AppendLine();
                }
            }

            // A compact "did the stabilizer beat rigid?" summary, pendulum model.
            sb.AppendLine("AI comparison excludes hold-fallback, mixed-policy-and-hold, policy-unverified and not-executed rows.");
            var pend = rows.Where(r => r.SloshModel == "pendulum" && r.ValidComparison).ToList();
            if (pend.Count > 0)
            {
                var ctrls = pend.Select(r => r.Controller).Distinct().ToList(); // first-seen order
                sb.AppendLine("### summary — spill[L], pendulum model");
                sb.AppendLine("  " + string.Format("{0,-24}", "drive") + "  " +
                    string.Join("  ", ctrls.Select(c => string.Format("{0,12}", Trim(c, 12)))));
                foreach (var byTrace in pend.GroupBy(r => r.Trace))
                {
                    var lookup = byTrace.ToDictionary(r => r.Controller, r => r.SpillLitres);
                    sb.AppendLine("  " + string.Format("{0,-24}", Trim(byTrace.Key, 24)) + "  " +
                        string.Join("  ", ctrls.Select(c =>
                            lookup.TryGetValue(c, out var v) ? string.Format("{0,12:F2}", v) : "    excluded")));
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
    }
}
