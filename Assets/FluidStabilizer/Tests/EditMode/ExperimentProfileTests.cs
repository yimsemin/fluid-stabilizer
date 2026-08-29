using System.IO;
using NUnit.Framework;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// The shared operating-point file (<c>experiment.txt</c>) must round-trip,
    /// tolerate a missing file, and actually reach <see cref="SimulationConfig"/>.
    /// </summary>
    public class ExperimentProfileTests
    {
        [Test]
        public void RoundTrips_ThroughText()
        {
            var p = new ExperimentProfile
            {
                FillRatio = 0.71, TankHeightZ = 0.55, RoadEnabled = false, RoadSeverity = 1.6,
                Driver = "aggressive", DriverIntensity = 0.8, EpisodeSeconds = 25.0,
                SloshModel = "shallow", Seed = 42,
            };

            string text;
            using (var w = new StringWriter()) { p.Save(w); text = w.ToString(); }

            ExperimentProfile q;
            using (var r = new StringReader(text)) q = ExperimentProfile.Load(r);

            Assert.AreEqual(p.FillRatio, q.FillRatio, 1e-9);
            Assert.AreEqual(p.TankHeightZ, q.TankHeightZ, 1e-9);
            Assert.AreEqual(p.RoadEnabled, q.RoadEnabled);
            Assert.AreEqual(p.RoadSeverity, q.RoadSeverity, 1e-9);
            Assert.AreEqual(p.Driver, q.Driver);
            Assert.AreEqual(p.DriverIntensity, q.DriverIntensity, 1e-9);
            Assert.AreEqual(p.EpisodeSeconds, q.EpisodeSeconds, 1e-9);
            Assert.AreEqual(p.SloshModel, q.SloshModel);
            Assert.AreEqual(p.Seed, q.Seed);
        }

        [Test]
        public void Load_IgnoresCommentsAndBlankLines_AndUnknownKeys()
        {
            const string text = @"
# a comment
fillRatio: 0.5   # inline comment
   road   =   off
bogusKey: 123
seed: 7
";
            using (var r = new StringReader(text))
            {
                ExperimentProfile p = ExperimentProfile.Load(r);
                Assert.AreEqual(0.5, p.FillRatio, 1e-9);
                Assert.IsFalse(p.RoadEnabled);
                Assert.AreEqual(7UL, p.Seed);
            }
        }

        [Test]
        public void LoadFromFileOrDefaults_WritesADefaultFile_WhenMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), "fs-exp-" + Path.GetRandomFileName() + ".txt");
            Assert.IsFalse(File.Exists(path));
            try
            {
                ExperimentProfile p = ExperimentProfile.LoadFromFileOrDefaults(path);
                Assert.AreEqual(new ExperimentProfile().FillRatio, p.FillRatio, 1e-9);
                Assert.IsTrue(File.Exists(path), "a default file should have been written");

                // second load reads the file it just wrote
                ExperimentProfile p2 = ExperimentProfile.LoadFromFileOrDefaults(path);
                Assert.AreEqual(p.Seed, p2.Seed);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void ToConfig_And_Factories_ApplyTheProfile()
        {
            var p = new ExperimentProfile
            {
                FillRatio = 0.68, TankHeightZ = 0.5, RoadEnabled = true, RoadSeverity = 1.3,
                Driver = "aggressive", SloshModel = "shallow", EpisodeSeconds = 12.0,
            };

            SimulationConfig c = p.ToConfig();
            Assert.AreEqual(0.68, c.Slosh.FillRatio, 1e-9);
            Assert.AreEqual(0.5, c.Slosh.TankHeightZ, 1e-9);
            Assert.IsTrue(c.Road.Enabled);
            Assert.AreEqual(1.3, c.Road.Severity, 1e-9);
            Assert.AreEqual(12.0, c.EpisodeSeconds, 1e-9);
            Assert.AreEqual(20.0, p.ToConfig(20.0).EpisodeSeconds, 1e-9);

            Assert.IsInstanceOf<RandomManeuverDriver>(p.MakeDriver(1));
            Assert.IsInstanceOf<ShallowWaterSloshModel>(p.MakeSlosh());
        }
    }
}
