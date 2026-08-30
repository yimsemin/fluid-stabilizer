using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Rl;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// Decision D8 / 묶음 3 — the lock-capable hybrid action. An active
    /// controller's "hold" can now be the mechanically locked joint (the rigid
    /// tank, ≈ 19 L on the recorded drives) instead of a sagging zero-command
    /// servo (≈ 25 L), closing the structural handicap finding F5 identified.
    /// </summary>
    public class LockCapableActionTests
    {
        // ---------------------------------------------------------------- LockGate

        private static GimbalConfig GateCfg() => new SimulationConfig().Gimbal;

        [Test]
        public void DisabledGate_NeverLocks()
        {
            var g = LockGate.Disabled;
            Assert.IsFalse(g.Enabled);
            for (int i = 0; i < 50; i++)
                Assert.IsFalse(g.ShouldLock(0.0, 0.0, 0.0, 0.0));
        }

        [Test]
        public void Gate_Locks_OnSmallDemand_NearZeroAngle()
        {
            var g = new LockGate(GateCfg());
            g.Reset();
            Assert.IsTrue(g.ShouldLock(0.0, 0.0, 0.0, 0.0), "should lock immediately when quiet and centred");
            Assert.IsTrue(g.IsLocked);
        }

        [Test]
        public void Gate_WillNotLock_WhenJointIsOffZero()
        {
            var cfg = GateCfg();
            var g = new LockGate(cfg);
            g.Reset();
            double bigAngle = cfg.LockAngleThresholdRad * 5.0;
            for (int i = 0; i < 20; i++)
                Assert.IsFalse(g.ShouldLock(0.0, 0.0, bigAngle, 0.0),
                    "must stay active while the servo is dragged off target — locking would snap it");
        }

        [Test]
        public void Gate_Hysteresis_HoldsLock_UntilDemandExceedsUnlock_AndDwellElapsed()
        {
            var cfg = GateCfg();
            var g = new LockGate(cfg);
            g.Reset();
            g.ShouldLock(0.0, 0.0, 0.0, 0.0); // now locked

            // Demand above the lock threshold but below the unlock threshold: stays locked.
            double between = cfg.LockRateThresholdRadPerSec * 1.5; // < LockRate * UnlockRateFactor (2.5)
            for (int i = 0; i < 30; i++)
                Assert.IsTrue(g.ShouldLock(between, 0.0, 0.0, 0.0), $"tick {i}: should hold the lock under weak demand");

            // Strong demand, but the minimum dwell must also have elapsed.
            double strong = cfg.LockRateThresholdRadPerSec * cfg.UnlockRateFactor * 2.0;
            var g2 = new LockGate(cfg);
            g2.Reset();
            g2.ShouldLock(0.0, 0.0, 0.0, 0.0);
            for (int i = 0; i < cfg.MinLockDwellTicks - 1; i++)
                Assert.IsTrue(g2.ShouldLock(strong, 0.0, 0.0, 0.0), $"tick {i}: locked until dwell elapses");
            Assert.IsFalse(g2.ShouldLock(strong, 0.0, 0.0, 0.0), "unlocks once dwell elapsed and demand is strong");
        }

        // --------------------------------------------------------- RlEnvironment

        private static RlEnvironment Env(bool lockCapable, double fill = 0.62, double seconds = 6.0)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = seconds };
            cfg.Slosh.FillRatio = fill;
            return new RlEnvironment(
                cfg,
                seed => new HighwayDriver(seed, briskness: 0.6),
                () => new PendulumSloshModel(),
                RewardWeights.Default(),
                frameStackLength: 6,
                baselineFactory: () => new HoldController(),
                residualRateScaleRadPerSec: 1.0,
                lockCapable: lockCapable);
        }

        private static double Signature(RlEnvironment env, ulong seed)
        {
            env.Reset(seed);
            var rng = new Random(11);
            double acc = 0;
            for (int i = 0; i < 200; i++)
            {
                var r = env.Step((float)(rng.NextDouble() * 0.1 - 0.05), (float)(rng.NextDouble() * 0.1 - 0.05));
                acc += r.Reward + r.Observation.Sum(x => (double)x);
                if (r.Terminated) break;
            }
            return acc + env.Summary().TotalOverflowVolume * 1000.0;
        }

        [Test]
        public void LockCapableEnv_NamesItself_AndIsDeterministic()
        {
            var env = Env(lockCapable: true);
            StringAssert.Contains("lock-capable", env.ActorName);
            Assert.AreEqual(Signature(env, 5), Signature(Env(true), 5), 1e-9);
        }

        [Test]
        public void LockCapableEnv_ZeroPolicy_EmitsLocked_NoActuatorTorque()
        {
            // baseline = Hold, policy does nothing → the composed command stays in
            // the dead-band → the actor locks → the actuator consumes no torque.
            var env = Env(lockCapable: true);
            env.Reset(4);
            double maxTorque = 0.0;
            for (int i = 0; i < 400; i++)
            {
                env.Step(0f, 0f);
                var g = env.GimbalState;
                maxTorque = Math.Max(maxTorque,
                    Math.Max(Math.Abs(g.Pitch.AppliedTorque), Math.Abs(g.Roll.AppliedTorque)));
            }
            Assert.AreEqual(0.0, maxTorque, 1e-9, "a locked joint must not apply actuator torque");
            Assert.AreEqual(0.0, env.Summary().TotalOverflowVolume, 1e-9);

            // The plain servo hold, by contrast, does work against the load.
            var servo = Env(lockCapable: false);
            servo.Reset(4);
            double servoTorque = 0.0;
            for (int i = 0; i < 400; i++)
            {
                servo.Step(0f, 0f);
                var g = servo.GimbalState;
                servoTorque = Math.Max(servoTorque,
                    Math.Max(Math.Abs(g.Pitch.AppliedTorque), Math.Abs(g.Roll.AppliedTorque)));
            }
            Assert.Greater(servoTorque, 0.0, "the zero-command servo should still fight the load");
        }

        // --------------------------------------------------------- hindsight gate

        private static DriverTrace DiscriminatingTrace(ulong seed = 3, double seconds = 6.0, double fill = 0.72)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = seconds };
            cfg.Slosh.FillRatio = fill;
            cfg.Road.Enabled = true;
            var trace = new DriverTrace
            {
                Label = "synthetic-" + seed, Seed = seed, StepSeconds = cfg.PhysicsStepSeconds,
                FillRatio = fill, RoadEnabled = true, RoadSeverity = 1.0, SloshModel = "pendulum",
            };
            var loop = new SimulationLoop(cfg, new HoldController(),
                new RecordingDriver(new RandomManeuverDriver(seed, 0.8), trace), new PendulumSloshModel());
            loop.RunEpisode(seed);
            return trace;
        }

        private static HindsightOptions FastOpts(bool lockCapable) => new HindsightOptions
        {
            KnotCount = 4,
            Restarts = 1,
            IterationsPerRestart = 8,
            LockCapable = lockCapable,
            EvalModels = new List<(string, Func<ISloshModel>)>
            {
                ("pendulum", () => new PendulumSloshModel()),
            },
        };

        [Test]
        public void Hindsight_LockCapable_DoNothingFloor_MatchesRigidHold_NotSaggingServo()
        {
            var trace = DiscriminatingTrace();

            HindsightDriveReport plain = HindsightOptimizer.Run(trace, FastOpts(lockCapable: false));
            HindsightDriveReport locked = HindsightOptimizer.Run(trace, FastOpts(lockCapable: true));

            var pRow = plain.Rows.Single(r => r.SloshModel == "pendulum");
            var lRow = locked.Rows.Single(r => r.SloshModel == "pendulum");

            // Lock-capable do-nothing = the rigid tank, so it cannot spill more
            // than the sagging servo, and it tracks the locked Hold floor.
            Assert.LessOrEqual(lRow.ZeroActionSpillLitres, pRow.ZeroActionSpillLitres + 1e-6,
                "lock-capable do-nothing must not spill more than the sagging servo");
            Assert.AreEqual(lRow.HoldSpillLitres, lRow.ZeroActionSpillLitres, 1.0,
                "lock-capable do-nothing should sit at the rigid Hold floor");
            Assert.LessOrEqual(lRow.CeilingSpillLitres, lRow.ZeroActionSpillLitres + 1e-6,
                "ceiling still never worse than doing nothing");
        }

        [Test]
        public void ConfigOverride_Reaches_TheEffectiveConfig()
        {
            var trace = DiscriminatingTrace();
            var opts = FastOpts(lockCapable: true);
            opts.ConfigOverride = c => { c.Slosh.FillRatio = 0.50; c.Slosh.TankHeightZ = 0.65; };

            SimulationConfig eff = opts.MakeConfig(trace);
            Assert.AreEqual(0.50, eff.Slosh.FillRatio, 1e-9);
            Assert.AreEqual(0.65, eff.Slosh.TankHeightZ, 1e-9);

            HindsightDriveReport r = HindsightOptimizer.Run(trace, opts);
            Assert.AreEqual(0.50, r.FillRatio, 1e-9, "report should echo the effective (overridden) fill");
        }
    }
}
