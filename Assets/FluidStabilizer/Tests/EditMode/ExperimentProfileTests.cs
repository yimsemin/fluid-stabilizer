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
                Preset = "눈길 (매우 미끄러움)",
                FillRatio = 0.71, TankHeightZ = 0.55, RoadEnabled = false, RoadSeverity = 1.6,
                RoadCurve = 2.2, RoadHills = 0.3, Grip = 0.42, SlipIntensity = 1.3,
                Driver = "aggressive", DriverIntensity = 0.8, EpisodeSeconds = 25.0,
                SloshModel = "shallow", Seed = 42,
            };

            ExperimentProfile q = p.Clone();

            Assert.AreEqual(p.Preset, q.Preset);
            Assert.AreEqual(p.FillRatio, q.FillRatio, 1e-9);
            Assert.AreEqual(p.TankHeightZ, q.TankHeightZ, 1e-9);
            Assert.AreEqual(p.RoadEnabled, q.RoadEnabled);
            Assert.AreEqual(p.RoadSeverity, q.RoadSeverity, 1e-9);
            Assert.AreEqual(p.RoadCurve, q.RoadCurve, 1e-9);
            Assert.AreEqual(p.RoadHills, q.RoadHills, 1e-9);
            Assert.AreEqual(p.Grip, q.Grip, 1e-9);
            Assert.AreEqual(p.SlipIntensity, q.SlipIntensity, 1e-9);
            Assert.AreEqual(p.Driver, q.Driver);
            Assert.AreEqual(p.DriverIntensity, q.DriverIntensity, 1e-9);
            Assert.AreEqual(p.EpisodeSeconds, q.EpisodeSeconds, 1e-9);
            Assert.AreEqual(p.SloshModel, q.SloshModel);
            Assert.AreEqual(p.Seed, q.Seed);
        }

        [Test]
        public void Presets_AreApplicable_AndChangeTheConfig()
        {
            var dry = new ExperimentProfile();
            Assert.IsTrue(dry.ApplyPreset("일반 고속도로"));
            var snow = new ExperimentProfile();
            Assert.IsTrue(snow.ApplyPreset("눈길 (매우 미끄러움)"));
            Assert.IsFalse(new ExperimentProfile().ApplyPreset("없는 프리셋"));

            Assert.AreEqual("눈길 (매우 미끄러움)", snow.Preset);
            Assert.Less(snow.Grip, dry.Grip, "snow should have less grip than highway");

            var cDry = dry.ToConfig();
            var cSnow = snow.ToConfig();
            Assert.Less(cSnow.Vehicle.FrictionLimit, cDry.Vehicle.FrictionLimit);
            Assert.Less(cSnow.Vehicle.MaxBrake, cDry.Vehicle.MaxBrake);
        }

        [Test]
        public void ToConfig_ScalesCurveHillsAndGrip()
        {
            var baseline = new SimulationConfig();
            var p = new ExperimentProfile { RoadCurve = 2.0, RoadHills = 0.5, Grip = 0.5 };
            var c = p.ToConfig();

            Assert.AreEqual(baseline.Road.CurvatureAmplitudeRadPerM * 2.0, c.Road.CurvatureAmplitudeRadPerM, 1e-12);
            Assert.AreEqual(baseline.Road.GradeAmplitudeRad * 0.5, c.Road.GradeAmplitudeRad, 1e-12);
            Assert.AreEqual(baseline.Vehicle.FrictionLimit * 0.5, c.Vehicle.FrictionLimit, 1e-12);
        }

        [Test]
        public void ToConfig_SlipIntensity_ScalesTheSlideDynamics_IndependentlyOfGrip()
        {
            var baseline = new SimulationConfig();

            var gentle = new ExperimentProfile { SlipIntensity = 0.5 }.ToConfig();
            Assert.AreEqual(baseline.Vehicle.SlipYawWander * 0.5, gentle.Vehicle.SlipYawWander, 1e-12);
            Assert.AreEqual(baseline.Vehicle.SlipGripLoss * 0.5, gentle.Vehicle.SlipGripLoss, 1e-12);
            Assert.AreEqual(baseline.Vehicle.SlipYawLag * 0.5, gentle.Vehicle.SlipYawLag, 1e-12);

            // 0 = the slide has no dynamics left (pre-묶음2b feel).
            var none = new ExperimentProfile { SlipIntensity = 0.0 }.ToConfig();
            Assert.AreEqual(0.0, none.Vehicle.SlipYawWander, 1e-12);
            Assert.AreEqual(0.0, none.Vehicle.SlipYawLag, 1e-12);

            // clamped to 3, and does not touch the friction limit (that is Grip's job).
            var wild = new ExperimentProfile { SlipIntensity = 9.0, Grip = 1.0 }.ToConfig();
            Assert.AreEqual(baseline.Vehicle.SlipYawWander * 3.0, wild.Vehicle.SlipYawWander, 1e-12);
            Assert.AreEqual(baseline.Vehicle.FrictionLimit, wild.Vehicle.FrictionLimit, 1e-12);
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
