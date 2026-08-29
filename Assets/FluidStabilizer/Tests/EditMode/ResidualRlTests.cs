using System;
using System.Linq;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Rl;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// experiment-plan.md step B — residual RL wiring. The env can compose
    /// <c>baseline + policy·residualScale</c> in joint-rate space; plain mode is
    /// unchanged, and both stay deterministic.
    /// </summary>
    public class ResidualRlTests
    {
        private static RlEnvironment Env(Func<IController> baseline, double fill = 0.62, double seconds = 6.0)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = seconds };
            cfg.Slosh.FillRatio = fill;
            return new RlEnvironment(
                cfg,
                seed => new HighwayDriver(seed, briskness: 0.6),
                () => new PendulumSloshModel(),
                RewardWeights.Default(),
                frameStackLength: 6,
                baselineFactory: baseline,
                residualRateScaleRadPerSec: baseline == null ? double.NaN : 1.0);
        }

        private static double RolloutSignature(RlEnvironment env, ulong seed)
        {
            env.Reset(seed);
            var rng = new Random(99);
            double acc = 0;
            for (int i = 0; i < 150; i++)
            {
                var r = env.Step((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
                acc += r.Reward + r.Observation.Sum(x => (double)x);
                if (r.Terminated) break;
            }
            return acc + env.Summary().TotalOverflowVolume * 1000.0;
        }

        [Test]
        public void PlainMode_KeepsItsName_AndIsDeterministic()
        {
            var env = Env(baseline: null);
            Assert.AreEqual("RL policy (joint-rate)", env.ActorName);
            Assert.AreEqual(RolloutSignature(env, 7), RolloutSignature(Env(null), 7), 1e-9);
        }

        [Test]
        public void ResidualMode_Hold_ComposesName_AndIsDeterministic()
        {
            var env = Env(() => new HoldController());
            StringAssert.Contains("residual", env.ActorName.ToLowerInvariant());
            StringAssert.Contains("Hold", env.ActorName);

            Assert.AreEqual(
                RolloutSignature(env, 3),
                RolloutSignature(Env(() => new HoldController()), 3),
                1e-9, "residual rollout is not deterministic");
        }

        [Test]
        public void ResidualMode_Sfa_RunsClean()
        {
            var env = Env(() => new SpecificForceAlignmentController());
            StringAssert.Contains("SpecificForceAlignment", env.ActorName);

            env.Reset(2);
            for (int i = 0; i < 300; i++)
            {
                var r = env.Step(0.05f, -0.05f);
                Assert.IsFalse(double.IsNaN(r.Reward));
                Assert.IsTrue(r.Observation.All(x => !float.IsNaN(x) && !float.IsInfinity(x)));
            }
        }

        [Test]
        public void ResidualMode_HoldBaseline_ZeroPolicy_KeepsJointsNearZero()
        {
            // baseline = Hold (PD toward 0), policy does nothing: the joints
            // should stay small and the tank should not overflow on a calm run.
            var env = Env(() => new HoldController());
            env.Reset(4);
            double maxAngle = 0.0;
            for (int i = 0; i < 400; i++)
            {
                env.Step(0f, 0f);
                var g = env.GimbalState;
                maxAngle = Math.Max(maxAngle, Math.Max(Math.Abs(g.Pitch.Angle), Math.Abs(g.Roll.Angle)));
            }
            Assert.Less(maxAngle, 0.15, "PD-hold baseline should keep the joints near zero");
            Assert.AreEqual(0.0, env.Summary().TotalOverflowVolume, 1e-9, "calm run should not spill");
        }
    }
}
