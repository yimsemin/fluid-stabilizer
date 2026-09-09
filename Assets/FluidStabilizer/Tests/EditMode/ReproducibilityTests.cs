using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Rl;
using FluidStabilizer.Sim;

namespace FluidStabilizer.Tests
{
    public class ReproducibilityTests
    {
        private static DriverTrace Record(int variant, out EpisodeResult original)
        {
            var p = new ExperimentProfile
            {
                FillRatio = 0.61, TankHeightZ = variant == 0 ? 0.43 : 0.79,
                Grip = variant == 0 ? 0.45 : 1.3, RoadSeverity = variant == 0 ? 0.37 : 1.53,
                RoadCurve = variant == 0 ? 1.8 : 0.2, RoadHills = variant == 0 ? 0.3 : 1.7,
                SlipIntensity = 1.4, RoadEnabled = true,
                EpisodeSeconds = variant == 0 ? 1.13 : 1.87, Seed = (ulong)(71 + variant),
                SloshModel = variant == 0 ? "pendulum" : "shallow",
            };
            var cfg = p.ToConfig();
            cfg.PhysicsRateHz = 300; cfg.SensorDecimation = 2; cfg.ControlDecimation = 4;
            cfg.Gimbal.PivotInVehicle = new Double3(2.3, 0.1, 1.2);
            cfg.Sensors.BundleLatencySeconds = 0.013;
            var trace = new DriverTrace { Label = "variant-" + variant };
            trace.CaptureConfig(cfg, p.Seed, p.SloshModel, "Hold");
            var loop = new SimulationLoop(cfg, new HoldController(),
                new RecordingDriver(new RandomManeuverDriver(p.Seed, 0.8), trace), p.MakeSlosh());
            original = loop.RunEpisode(p.Seed);
            var text = new StringWriter(); trace.Save(text);
            return DriverTrace.Load(new StringReader(text.ToString()));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void NonDefaultRecording_MatchesReplayComparisonAndBatch(int variant)
        {
            var trace = Record(variant, out var original);
            Assert.IsEmpty(trace.Assumptions);
            var go = new GameObject("repro-test");
            try
            {
                var runner = go.AddComponent<SimulationRunner>();
                runner.UseExperimentProfile = true; // replay takes priority over this and all inspector knobs
                runner.FillRatio = 0.1; runner.Seed = 999; runner.Road = false;
                runner.ReplayTrace = trace;
                runner.Stabilizer = StabilizerChoice.Off;
                runner.BuildLoop();
                while (runner.Loop.StepPhysics()) { }
                var replay = runner.Loop.Result();
                Assert.AreEqual(original.TotalOverflowVolume, replay.TotalOverflowVolume);
                Assert.AreEqual(original.SloshEnergyIntegral, replay.SloshEnergyIntegral);
                Assert.AreEqual(original.WorstFreeboard, replay.WorstFreeboard);
                Assert.AreEqual(trace.Count, replay.PhysicsSteps);
                Assert.AreEqual(trace.Seed, replay.Seed);
                Assert.AreEqual(ConfigSnapshot.Encode(trace.ToConfig()), ConfigSnapshot.Encode(runner.Loop.Config));
                var cmp = go.AddComponent<ComparisonRunner>();
                cmp.Lanes = new[] { StabilizerChoice.Off };
                cmp.StartComparison(trace, false);
                Assert.AreEqual(original.TotalOverflowVolume * 1000, cmp.RunToEndHeadless()[StabilizerChoice.Off]);
                var rows = TraceEvaluator.Run(new[] { trace }, new[] { ("Hold", (Func<SimulationConfig, IController>)(_ => new HoldController())) }, null);
                Assert.AreEqual(original.TotalOverflowVolume * 1000, rows[0].SpillLitres);
                Assert.AreEqual(original.SloshEnergyIntegral, rows[0].SloshEnergyIntegral);
                Assert.AreEqual(trace.SloshModel, rows[0].SloshModel);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void LegacyMissingSettings_AreAssumptions_NotSavedAsRecordedFacts()
        {
            const string old = "# fluid-stabilizer-driver-trace/v1\n# label: old\n# seed: 7\n# stepSeconds: 0.01\n# fillRatio: 0.62\n# roadEnabled: 1\n# roadSeverity: 0.37\n# sloshModel: pendulum\nthrottle,brake,steer\n1,0,0\n";
            var trace = DriverTrace.Load(new StringReader(old));
            Assert.AreEqual(0.37, trace.ToConfig().Road.Severity);
            StringAssert.Contains("unknown-at-recording:Slosh.TankHeightZ=", trace.Assumptions);
            StringAssert.Contains("Vehicle.FrictionLimit=", trace.Assumptions);
            var writer = new StringWriter(); trace.Save(writer);
            StringAssert.DoesNotContain("# config.", writer.ToString());
            Assert.AreEqual(trace.Assumptions, DriverTrace.Load(new StringReader(writer.ToString())).Assumptions);
        }

        [Test]
        public void DatasetDetectsRenamedInputDuplicatesAcrossSplits()
        {
            var a = Record(0, out _); var b = Record(0, out _);
            a.DatasetSplit = "train"; b.DatasetSplit = "final";
            b.Label = "renamed-unseen"; b.Seed = 999; b.RecordedConfig["Slosh.TankHeightZ"] = "0.8";
            Assert.Throws<InvalidDataException>(() => TraceDataset.ValidateUnique(new[] { a, b }));
        }

        // The manifest is the only thing standing between "train" and "final".
        // Every way it can silently lie must fail loudly instead.
        [Test]
        public void Manifest_FailsOnTamperedSource_EmptySplit_AndSeenFinal()
        {
            string dir = Path.Combine(Path.GetTempPath(), "repro-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                var t = Record(0, out _);
                string file = Path.Combine(dir, "drive-a.trace.csv");
                t.SaveToFile(file);
                string sha = ConfigSnapshot.FileHash(file);
                string manifest = Path.Combine(dir, "m.tsv");

                void Write(string body) { File.Delete(manifest); File.WriteAllText(manifest, "split\tfile\tsourceSha256\thistory\n" + body); }

                Write($"train\tdrive-a.trace.csv\t{sha}\tbc\n");
                Assert.AreEqual(1, TraceDataset.Load(manifest, dir, "train").Count);

                // a split nobody assigned drives to must not silently evaluate on nothing
                Assert.Throws<InvalidDataException>(() => TraceDataset.Load(manifest, dir, "final"));

                // a drive with a known history must not be laundered into the final set
                Write($"final\tdrive-a.trace.csv\t{sha}\tbc-and-repeated-condition-search\n");
                Assert.Throws<InvalidDataException>(() => TraceDataset.Load(manifest, dir, "final"));

                // the recorded drive changed underneath the manifest
                Write($"train\tdrive-a.trace.csv\t{new string('0', 64)}\tbc\n");
                Assert.Throws<InvalidDataException>(() => TraceDataset.Load(manifest, dir, "train"));

                Write("train\tsub/drive-a.trace.csv\t" + sha + "\tbc\n");
                Assert.Throws<InvalidDataException>(() => TraceDataset.Load(manifest, dir, "train"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void ShippedManifest_LoadsAndAssignsNoFinalDrives()
        {
            if (!File.Exists("datasets.tsv") || !Directory.Exists("Traces"))
                Assert.Ignore("no local Traces/ — recorded drives are not committed");
            var train = TraceDataset.Load("datasets.tsv", "Traces", "train");
            Assert.IsNotEmpty(train);
            CollectionAssert.AllItemsAreUnique(train.Select(t => t.InputSha256).ToList());
            // documented in docs/reproducibility.md: a real holdout does not exist yet
            Assert.Throws<InvalidDataException>(() => TraceDataset.Load("datasets.tsv", "Traces", "final"));
        }

        [Test]
        public void TraceFileSave_RefusesToOverwrite()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".trace.csv");
            try
            {
                File.WriteAllText(path, "original");
                Assert.Throws<IOException>(() => new DriverTrace().SaveToFile(path));
                Assert.AreEqual("original", File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void RlReset_UsesEachTraceConfigSeedModelAndFullLength()
        {
            var traces = new[] { Record(0, out _), Record(1, out _) };
            var env = new RlEnvironment(new SimulationConfig(), _ => new ScriptedManeuverDriver(),
                traces[0].MakeSloshModel, new RewardWeights(), traceSelector: s => traces[(int)(s % 2)]);
            for (ulong selection = 0; selection < 2; selection++)
            {
                var trace = traces[(int)selection];
                env.Reset(selection);
                RlStepResult result;
                int guard = 1000;
                do { result = env.Step(0, 0); } while (!result.Truncated && --guard > 0);
                Assert.Greater(guard, 0);
                Assert.AreEqual(trace.Count, env.Summary().PhysicsSteps);
                Assert.AreEqual(trace.Seed, env.Summary().Seed);
                Assert.AreEqual(trace.MakeSloshModel().Name, env.Summary().SloshModelName);
                Assert.AreEqual(ConfigSnapshot.Encode(trace.ToConfig()), ConfigSnapshot.Encode(env.EffectiveConfig));
            }
        }
    }
}
