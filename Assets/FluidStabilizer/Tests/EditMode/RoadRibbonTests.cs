using NUnit.Framework;
using UnityEngine;
using FluidStabilizer.Sim;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// experiment-plan.md step 2b. The ribbon is cosmetic, but it must draw the
    /// <em>same</em> road the physics feels — it rebuilds an independent
    /// <see cref="RoadModel"/> from the loop's seed and config.
    /// </summary>
    public class RoadRibbonTests
    {
        [Test]
        public void RoadReplica_FromSeedAndConfig_MatchesWhatThePhysicsFeels()
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 6.0 };
            cfg.Road.Enabled = true;
            cfg.Road.Severity = 1.3;
            const ulong seed = 909;

            var loop = new SimulationLoop(cfg, new HoldController(),
                new RandomManeuverDriver(seed, 0.5), new PendulumSloshModel());
            loop.BeginEpisode(seed);

            // The replica RoadRibbon builds — same construction as the loop's own.
            var replica = new RoadModel(cfg.Road);
            replica.Reset(new DeterministicRandom(seed).Fork("road"));

            int checks = 0;
            double sampledAt = loop.DistanceTraveled; // the loop samples the road at the pre-step distance
            for (int i = 0; i < 3600; i++)
            {
                if (!loop.StepPhysics()) break;
                if (i % 300 == 0)
                {
                    double feltSurface = loop.LatestGroundTruth.RoadSurfaceHeight;
                    double replicaSurface = replica.Sample(sampledAt).SurfaceHeight;
                    Assert.AreEqual(feltSurface, replicaSurface, 1e-9,
                        $"replica road diverged from the physics road at s={sampledAt:F2} m");
                    checks++;
                }
                sampledAt = loop.DistanceTraveled;
            }
            Assert.Greater(checks, 3, "expected several comparison points");
        }

        [Test]
        public void Ribbon_BuildsAMesh_NoNaN_InBothModes()
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 4.0 };
            cfg.Road.Enabled = true;
            var loop = new SimulationLoop(cfg, new HoldController(),
                new ConstantDriver(new Contracts.DriverControls { Throttle = 0.3 }), new PendulumSloshModel());
            loop.BeginEpisode(3);
            for (int i = 0; i < 600; i++) loop.StepPhysics();

            var go = new GameObject("ribbon-test");
            try
            {
                var rr = go.AddComponent<RoadRibbon>();
                rr.LoopOverride = loop;
                rr.RenderTick();

                Assert.IsNotNull(rr.DebugMesh, "no mesh built");
                Assert.IsTrue(rr.DebugStrip.gameObject.activeSelf, "strip should be visible with road on");
                var vs = rr.DebugMesh.vertices;
                Assert.Greater(vs.Length, 100);
                foreach (var p in vs)
                    Assert.IsFalse(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z), "NaN vertex");

                // Road off → still drawn (a plain flat surface, so the truck has
                // ground under it), no NaN.
                var cfg2 = new SimulationConfig { EpisodeSeconds = 4.0 }; // road disabled by default
                var loop2 = new SimulationLoop(cfg2, new HoldController(),
                    new ConstantDriver(new Contracts.DriverControls { Throttle = 0.3 }), new PendulumSloshModel());
                loop2.BeginEpisode(3);
                loop2.StepPhysics();
                rr.LoopOverride = loop2;
                rr.RenderTick();
                Assert.IsTrue(rr.DebugStrip.gameObject.activeSelf, "strip stays visible (plain) with road off");
                foreach (var p in rr.DebugMesh.vertices)
                    Assert.IsFalse(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z), "NaN vertex (road off)");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
