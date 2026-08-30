using System.IO;
using System.Linq;
using NUnit.Framework;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Eval;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// `EpisodeLog` — one CSV row per finished episode, appended so results
    /// accumulate across runs (design-review §13). Header once, stable columns,
    /// every field a single clean cell.
    /// </summary>
    public class EpisodeLogTests
    {
        private static EpisodeResult Sample(double spillM3 = 0.012, string ctrl = "Rigid (Hold, locked)") =>
            new EpisodeResult
            {
                Seed = 7,
                PhysicsSteps = 18000,
                DurationSeconds = 30.0,
                ControllerName = ctrl,
                DriverName = "recorded: drive-x",
                SloshModelName = "pendulum",
                TotalOverflowVolume = spillM3,
                WorstFreeboard = -0.03,
                SloshEnergyIntegral = 42.5,
                MeanAbsActionRate = 0.0,
                TorqueSaturationFraction = 0.0,
                AngleLimitFraction = 0.0,
            };

        [Test]
        public void WritesHeaderOnce_ThenOneRowPerEpisode()
        {
            var sw = new StringWriter();
            using (var log = new EpisodeLog(sw, "run-1", writeHeader: true))
            {
                var cfg = new SimulationConfig { EpisodeSeconds = 30 };
                cfg.Slosh.FillRatio = 0.62;
                log.Add("trace-eval", cfg, Sample());
                log.Add("trace-eval", cfg, Sample(0.0, "RL residual on Rigid (Hold, locked) (lock-capable)"));
            }

            string[] lines = sw.ToString().Trim().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            Assert.AreEqual(3, lines.Length, "header + 2 rows");
            StringAssert.StartsWith("utc,run,source,controller", lines[0]);

            int cols = lines[0].Split(',').Length;
            foreach (string row in lines.Skip(1))
                Assert.AreEqual(cols, row.Split(',').Length, $"row has wrong column count: {row}");
        }

        [Test]
        public void CommaInAName_DoesNotBreakColumns()
        {
            var sw = new StringWriter();
            using (var log = new EpisodeLog(sw, "run,with,commas", writeHeader: true))
                log.Add("src", new SimulationConfig(), Sample(0.01, "Weird, Name, controller"));

            string[] lines = sw.ToString().Trim().Split('\n');
            int cols = lines[0].Split(',').Length;
            Assert.AreEqual(cols, lines[1].Split(',').Length);
            StringAssert.DoesNotContain("Weird, Name", sw.ToString());
            StringAssert.Contains("Weird; Name; controller", sw.ToString());
        }

        [Test]
        public void FileMode_AppendsWithoutDuplicatingHeader()
        {
            string path = Path.Combine(Path.GetTempPath(), "fluidstab-episodelog-test-" + System.Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                var cfg = new SimulationConfig();
                using (var a = new EpisodeLog(path, "run-a")) a.Add("s", cfg, Sample());
                using (var b = new EpisodeLog(path, "run-b")) b.Add("s", cfg, Sample());

                string[] lines = File.ReadAllLines(path);
                Assert.AreEqual(3, lines.Length, "one header + two appended rows");
                Assert.AreEqual(1, lines.Count(l => l.StartsWith("utc,run,source")));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
