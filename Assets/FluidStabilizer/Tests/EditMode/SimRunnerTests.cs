using NUnit.Framework;
using UnityEngine;
using FluidStabilizer.Sim;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Tests
{
    public class SimRunnerTests
    {
        [Test]
        public void Runner_StepsInRealTime_WithoutExceptions_AndAdvancesState()
        {
            var go = new GameObject("runner-test");
            try
            {
                var runner = go.AddComponent<SimulationRunner>();
                runner.FillRatio = 0.55;
                runner.Stabilizer = StabilizerChoice.SpecificForceAlignment;
                runner.BuildLoop();

                // 5 s of wall time at 60 fps.
                for (int i = 0; i < 300; i++) runner.Advance(1.0 / 60.0);

                Assert.Greater(runner.Loop.SimTime, 4.9);
                Assert.IsFalse(runner.Loop.EpisodeComplete, "endless config should never complete");
                Assert.IsFalse(double.IsNaN(runner.Loop.VehicleState.Speed));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Runner_SwitchStabilizerLive_KeepsRunning()
        {
            var go = new GameObject("runner-test-2");
            try
            {
                var runner = go.AddComponent<SimulationRunner>();
                runner.BuildLoop();
                for (int i = 0; i < 60; i++) runner.Advance(1.0 / 60.0);

                runner.SetStabilizer(StabilizerChoice.PrivilegedFeedforward);
                Assert.AreEqual(StabilizerChoice.PrivilegedFeedforward, runner.ActiveStabilizer);

                for (int i = 0; i < 60; i++) runner.Advance(1.0 / 60.0);
                Assert.Greater(runner.Loop.SimTime, 1.9);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Runner_ShallowWaterModel_StepsAndSwitchesLive()
        {
            var go = new GameObject("runner-test-3");
            try
            {
                var runner = go.AddComponent<SimulationRunner>();
                runner.SloshModel = SloshModelChoice.ShallowWater;
                runner.BuildLoop();
                Assert.AreEqual(SloshModelChoice.ShallowWater, runner.ActiveSloshModel);
                Assert.AreEqual("Shallow water (2-D Saint-Venant)", runner.Loop.SloshModel.Name);

                for (int i = 0; i < 120; i++) runner.Advance(1.0 / 60.0);
                Assert.Greater(runner.Loop.SimTime, 1.9);
                Assert.IsFalse(double.IsNaN(runner.Loop.LatestGroundTruth.SloshKineticEnergy));

                runner.SetSloshModel(SloshModelChoice.PendulumRom);
                Assert.AreEqual(SloshModelChoice.PendulumRom, runner.ActiveSloshModel);
                for (int i = 0; i < 60; i++) runner.Advance(1.0 / 60.0);
                Assert.Greater(runner.Loop.SimTime, 0.9);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Frames_RoundTripsAndFlipsHandedness()
        {
            var v = new Double3(1, 2, 3); // forward, left, up
            Vector3 u = Frames.ToUnity(v);
            Assert.AreEqual(new Vector3(-2, 3, 1), u);      // (-y, z, x)
            Double3 back = Frames.ToSim(u);
            Assert.AreEqual(v, back);
        }
    }
}
