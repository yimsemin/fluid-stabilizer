using System.IO;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// Phase-1 item 3: a recorded human drive must replay bit-for-bit, so it can
    /// serve as both training disturbance data and a held-out evaluation
    /// condition (design-review §17, project-goals ③).
    /// </summary>
    public class DriverTraceTests
    {
        private static SimulationConfig Cfg()
        {
            var c = new SimulationConfig { EpisodeSeconds = 8.0 };
            c.Slosh.FillRatio = 0.6;
            c.Road.Enabled = true;      // exercise the full deterministic pipeline
            return c;
        }

        private static double Checksum(GroundTruthFrame gt, double acc) =>
            acc * 1.0000001
            + gt.VehicleSpeed
            + gt.PitchAngle * 7.0
            + gt.RollAngle * 13.0
            + gt.SloshKineticEnergy * 3.0
            + gt.CumulativeOverflowVolume * 101.0
            + gt.MinFreeboard * 17.0;

        [Test]
        public void RecordThenReplay_ReproducesTheEpisode_BitForBit()
        {
            const ulong seed = 4242;

            // --- record a stochastic (but seeded) drive through the recorder ---
            var trace = new DriverTrace { Label = "t", Seed = seed, StepSeconds = Cfg().PhysicsStepSeconds, FillRatio = 0.6, RoadEnabled = true };
            var rec = new RecordingDriver(new RandomManeuverDriver(seed, aggression: 0.7), trace);

            var recLoop = new SimulationLoop(Cfg(), new SpecificForceAlignmentController(), rec, new PendulumSloshModel());
            recLoop.BeginEpisode(seed);
            double recChecksum = 0;
            while (recLoop.StepPhysics()) recChecksum = Checksum(recLoop.LatestGroundTruth, recChecksum);

            Assert.AreEqual(recLoop.Config.TotalPhysicsSteps, trace.Count, "one sample per physics step");

            // --- round-trip the trace through the text file format ---
            var sw = new StringWriter();
            trace.Save(sw);
            DriverTrace reloaded = DriverTrace.Load(new StringReader(sw.ToString()));

            Assert.AreEqual(trace.Count, reloaded.Count);
            Assert.AreEqual(seed, reloaded.Seed);
            Assert.AreEqual(trace.StepSeconds, reloaded.StepSeconds, 0.0);
            Assert.IsTrue(reloaded.RoadEnabled);

            // --- replay the reloaded trace over a fresh loop ---
            var replayLoop = new SimulationLoop(Cfg(), new SpecificForceAlignmentController(),
                new RecordedTraceDriver(reloaded), new PendulumSloshModel());
            replayLoop.BeginEpisode(seed);
            double replayChecksum = 0;
            while (replayLoop.StepPhysics()) replayChecksum = Checksum(replayLoop.LatestGroundTruth, replayChecksum);

            Assert.AreEqual(recChecksum, replayChecksum, "replayed drive must be bit-identical to the recorded one");

            var a = recLoop.Result();
            var b = replayLoop.Result();
            Assert.AreEqual(a.TotalOverflowVolume, b.TotalOverflowVolume, 0.0);
            Assert.AreEqual(a.WorstFreeboard, b.WorstFreeboard, 0.0);
            Assert.AreEqual(a.SloshEnergyIntegral, b.SloshEnergyIntegral, 0.0);
        }

        [Test]
        public void SaveFormat_IsTextWithAHeaderAndOneRowPerStep()
        {
            var trace = new DriverTrace { Label = "demo", Seed = 9 };
            trace.Add(new DriverControls { Throttle = 0.25, Brake = 0, Steer = -0.5 });
            trace.Add(new DriverControls { Throttle = 0, Brake = 0.8, Steer = 0.125 });

            var sw = new StringWriter();
            trace.Save(sw);
            string text = sw.ToString();

            StringAssert.Contains(DriverTrace.FormatId, text);
            StringAssert.Contains("# label: demo", text);
            StringAssert.Contains("throttle,brake,steer", text);

            DriverTrace back = DriverTrace.Load(new StringReader(text));
            Assert.AreEqual(2, back.Count);
            Assert.AreEqual(0.8, back.Samples[1].Brake, 0.0);
            Assert.AreEqual(-0.5, back.Samples[0].Steer, 0.0);
        }

        [Test]
        public void RecordedTraceDriver_HoldsTheLastSample_PastTheEnd()
        {
            var trace = new DriverTrace { StepSeconds = 0.01 };
            trace.Add(new DriverControls { Steer = 0.1 });
            trace.Add(new DriverControls { Steer = 0.2 });
            trace.Add(new DriverControls { Steer = 0.3 });
            var d = new RecordedTraceDriver(trace);

            Assert.AreEqual(0.1, d.Sample(0.01).Steer, 0.0);
            Assert.AreEqual(0.3, d.Sample(0.03).Steer, 0.0);
            Assert.AreEqual(0.3, d.Sample(5.0).Steer, 0.0, "runs out → holds the last sample");
        }

        [Test]
        public void EmptyTrace_ReplaysAsNeutral()
        {
            var d = new RecordedTraceDriver(new DriverTrace());
            DriverControls c = d.Sample(1.0);
            Assert.AreEqual(0.0, c.Throttle + c.Brake + c.Steer, 0.0);
        }

        [Test]
        public void ToConfig_MatchesTheRecordedSetup()
        {
            var trace = new DriverTrace { StepSeconds = 1.0 / 600.0, FillRatio = 0.7, RoadEnabled = true, RoadSeverity = 1.5 };
            for (int i = 0; i < 600; i++) trace.Add(DriverControls.Neutral);

            SimulationConfig c = trace.ToConfig();
            Assert.AreEqual(600.0, c.PhysicsRateHz, 1e-9);
            Assert.AreEqual(1.0, c.EpisodeSeconds, 1e-9);
            Assert.AreEqual(0.7, c.Slosh.FillRatio, 0.0);
            Assert.IsTrue(c.Road.Enabled);
            Assert.AreEqual(1.5, c.Road.Severity, 0.0);
        }
    }
}
