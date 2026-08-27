using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim.Eval
{
    public struct AggregateResult
    {
        public string Controller;
        public string Scenario;
        public int Episodes;
        public double MeanOverflow;      // m³
        public double StdOverflow;
        public double MaxOverflow;
        public int SpillEpisodes;        // episodes with overflow > 1e-5 m³
        public double MeanWorstFreeboard;
        public double MeanSloshEnergyIntegral;
        public double MeanEffort;
        public double MeanTorqueSatFraction;
    }

    /// <summary>
    /// Runs each controller across each scenario over a fixed seed set and
    /// aggregates the metrics. Controllers and drivers are created per episode
    /// via factories so every run starts clean and the same interface is reused
    /// by the RL evaluation later.
    /// </summary>
    public static class Evaluator
    {
        private const double SpillThreshold = 1e-5;

        public static List<AggregateResult> Run(
            IReadOnlyList<Scenario> scenarios,
            IReadOnlyList<(string name, Func<IController> make)> controllers,
            IReadOnlyList<ulong> seeds)
        {
            var results = new List<AggregateResult>();

            foreach (var scenario in scenarios)
            foreach (var (name, make) in controllers)
            {
                var overflow = new List<double>(seeds.Count);
                double fbSum = 0, energySum = 0, effortSum = 0, satSum = 0;

                foreach (ulong seed in seeds)
                {
                    var loop = new SimulationLoop(
                        scenario.Config(), make(), scenario.Driver(seed), scenario.Slosh());
                    EpisodeResult r = loop.RunEpisode(seed);

                    overflow.Add(r.TotalOverflowVolume);
                    fbSum += r.WorstFreeboard;
                    energySum += r.SloshEnergyIntegral;
                    effortSum += r.MeanAbsActionRate;
                    satSum += r.TorqueSaturationFraction;
                }

                double mean = overflow.Average();
                double var = overflow.Count > 1
                    ? overflow.Sum(x => (x - mean) * (x - mean)) / (overflow.Count - 1)
                    : 0.0;

                results.Add(new AggregateResult
                {
                    Controller = name,
                    Scenario = scenario.Name,
                    Episodes = seeds.Count,
                    MeanOverflow = mean,
                    StdOverflow = Math.Sqrt(var),
                    MaxOverflow = overflow.Max(),
                    SpillEpisodes = overflow.Count(x => x > SpillThreshold),
                    MeanWorstFreeboard = fbSum / seeds.Count,
                    MeanSloshEnergyIntegral = energySum / seeds.Count,
                    MeanEffort = effortSum / seeds.Count,
                    MeanTorqueSatFraction = satSum / seeds.Count
                });
            }

            return results;
        }

        public static string FormatReport(IReadOnlyList<AggregateResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("fluid-stabilizer — controller evaluation");
            sb.AppendLine($"generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine();

            foreach (var group in results.GroupBy(r => r.Scenario))
            {
                sb.AppendLine($"### {group.Key}   ({group.First().Episodes} seeds)");
                sb.AppendLine("  controller                     meanSpill[L]   maxSpill[L]  spill/eps   worstFB[m]   sloshE∫   effort   torqueSat");
                sb.AppendLine("  " + new string('-', 118));
                foreach (var r in group.OrderBy(r => r.MeanOverflow))
                {
                    sb.AppendLine(string.Format(
                        "  {0,-28}  {1,12:F4}  {2,11:F4}  {3,7}/{4,-3}  {5,10:F4}  {6,8:F2}  {7,7:F3}  {8,8:P0}",
                        Trim(r.Controller, 28),
                        r.MeanOverflow * 1000.0,
                        r.MaxOverflow * 1000.0,
                        r.SpillEpisodes, r.Episodes,
                        r.MeanWorstFreeboard,
                        r.MeanSloshEnergyIntegral,
                        r.MeanEffort,
                        r.MeanTorqueSatFraction));
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
    }
}
