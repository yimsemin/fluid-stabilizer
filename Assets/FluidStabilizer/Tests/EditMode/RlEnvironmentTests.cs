using System;
using System.Linq;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Rl;

namespace FluidStabilizer.Tests
{
    public class RlEnvironmentTests
    {
        private static RlEnvironment MakeEnv(int stack = 6, RewardWeights? w = null) => new RlEnvironment(
            new SimulationConfig { EpisodeSeconds = 6.0 },
            seed => new HighwayDriver(seed, briskness: 0.7),
            () => new PendulumSloshModel(),
            w ?? RewardWeights.Default(),
            frameStackLength: stack);

        [Test]
        public void Observation_HasStackedShape_AndIsFinite()
        {
            var env = MakeEnv(stack: 6);
            Assert.AreEqual(ObservationEncoder.SingleFrameSize * 6, env.ObservationSize);
            Assert.AreEqual(PrivilegedEncoder.Size, env.PrivilegedObservationSize);
            Assert.AreEqual(2, env.ActionSize);

            float[] obs = env.Reset(1);
            Assert.AreEqual(env.ObservationSize, obs.Length);
            Assert.IsTrue(obs.All(x => !float.IsNaN(x) && !float.IsInfinity(x)));

            // before the stack fills, the oldest frames are zero
            Assert.IsTrue(obs.Take(ObservationEncoder.SingleFrameSize * 5).All(x => x == 0f));
            Assert.IsTrue(obs.Skip(ObservationEncoder.SingleFrameSize * 5).Any(x => x != 0f));
        }

        [Test]
        public void FrameStack_FillsAndSlides()
        {
            var env = MakeEnv(stack: 4);
            env.Reset(2);
            float[] obs = null;
            for (int i = 0; i < 10; i++) obs = env.Step(0.1f, -0.1f).Observation;

            // after ≥ 4 steps every frame slot carries data
            for (int k = 0; k < 4; k++)
            {
                var frame = obs.Skip(k * ObservationEncoder.SingleFrameSize)
                               .Take(ObservationEncoder.SingleFrameSize);
                Assert.IsTrue(frame.Any(x => x != 0f), $"frame {k} is all zero");
            }
        }

        [Test]
        public void Step_IsDeterministic_GivenSeedAndActions()
        {
            double RunSum(ulong seed)
            {
                var env = MakeEnv();
                env.Reset(seed);
                double acc = 0;
                var rng = new Random(1234); // fixed action sequence
                for (int i = 0; i < 120; i++)
                {
                    var r = env.Step((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
                    acc += r.Reward + r.Observation.Sum(x => (double)x)
                                    + r.PrivilegedObservation.Sum(x => (double)x);
                    if (r.Terminated) break;
                }
                return acc;
            }

            Assert.AreEqual(RunSum(7), RunSum(7), 0.0, "same seed + same actions ⇒ bit-identical");
            Assert.AreNotEqual(RunSum(7), RunSum(8), "different seed ⇒ different rollout");
        }

        [Test]
        public void Reward_IsMostlyNonPositive_AndPenalisesSpill()
        {
            // A calm run: rewards should hover near zero (small costs only), no spill.
            var env = MakeEnv();
            env.Reset(3);
            double worst = 0, best = double.NegativeInfinity;
            bool anyTerminated = false;
            for (int i = 0; i < 200; i++)
            {
                var r = env.Step(0f, 0f); // do nothing
                worst = Math.Min(worst, r.Reward);
                best = Math.Max(best, r.Reward);
                anyTerminated |= r.Terminated;
            }
            Assert.LessOrEqual(best, 1.0e-6, "with only cost terms the best step reward is ~0");
            Assert.IsFalse(anyTerminated, "a calm highway run should not hit the terminal-spill failure");

            // A forced hard oscillation into a low-freeboard tank: expect real negative reward.
            var hard = new RlEnvironment(
                Cfg(0.72), seed => new RandomManeuverDriver(seed, aggression: 0.9),
                () => new PendulumSloshModel(), RewardWeights.Default(), 6);
            hard.Reset(5);
            double minReward = 0;
            for (int i = 0; i < 400; i++)
            {
                float a = (float)Math.Sin(i * 0.4);
                minReward = Math.Min(minReward, hard.Step(a, -a).Reward);
            }
            Assert.Less(minReward, -0.05, "hard sloshing at low freeboard should produce clearly negative reward");
        }

        [Test]
        public void TerminalSpill_EndsTheEpisode_Once()
        {
            var w = RewardWeights.Default();
            w.TerminalSpillVolume = 1e-6; // trip on the first drop
            w.TerminalSpill = 3.0;
            var env = new RlEnvironment(Cfg(0.8), seed => new RandomManeuverDriver(seed, aggression: 1.0),
                () => new PendulumSloshModel(), w, 6);
            env.Reset(1);

            int terminatedCount = 0;
            for (int i = 0; i < 600; i++)
            {
                float a = (float)Math.Sin(i * 0.5);
                var r = env.Step(a, -a);
                if (r.Terminated)
                {
                    terminatedCount++;
                    Assert.Less(r.Reward, -1.0, "the terminal step carries the K_term penalty");
                }
            }
            Assert.AreEqual(1, terminatedCount, "terminal-spill fires exactly once");
        }

        [Test]
        public void EpisodeResult_IsPopulated_AfterARollout()
        {
            var env = MakeEnv();
            env.Reset(4);
            for (int i = 0; i < 200; i++) env.Step(0f, 0f); // zero action = a zero-rate servo, not locked Hold
            var res = env.Summary();
            Assert.Greater(res.PhysicsSteps, 0);
            Assert.AreEqual("RL policy (joint-rate)", res.ControllerName);
            Assert.AreEqual(0.0, res.TotalOverflowVolume, 1e-9, "a calm run should not spill");
        }

        private static SimulationConfig Cfg(double fill)
        {
            var c = new SimulationConfig { EpisodeSeconds = 12.0 };
            c.Slosh.FillRatio = fill;
            return c;
        }
    }
}
