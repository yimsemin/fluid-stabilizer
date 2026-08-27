using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    public class SimulationLoopTests
    {
        private static SimulationConfig ShortConfig()
        {
            var c = new SimulationConfig { EpisodeSeconds = 6.0 };
            return c;
        }

        [Test]
        public void Episode_RunsToConfiguredLength()
        {
            var cfg = ShortConfig();
            var loop = new SimulationLoop(cfg, new HoldController(), new ScriptedManeuverDriver());
            var result = loop.RunEpisode(1);

            Assert.AreEqual(cfg.TotalPhysicsSteps, result.PhysicsSteps);
            Assert.AreEqual(6.0, result.DurationSeconds, 1e-9);
            Assert.IsTrue(loop.EpisodeComplete);
        }

        [Test]
        public void SameSeed_ProducesBitIdenticalTrace()
        {
            double Run(ulong seed)
            {
                var loop = new SimulationLoop(ShortConfig(), new HoldController(), new ScriptedManeuverDriver());
                loop.BeginEpisode(seed);
                double checksum = 0;
                while (loop.StepPhysics())
                {
                    var gt = loop.LatestGroundTruth;
                    checksum = checksum * 1.0000001
                               + gt.VehicleSpeed
                               + gt.PitchAngle * 7.0
                               + gt.RollAngle * 13.0
                               + gt.TankSpecificForceTankFrame.X
                               + gt.TankSpecificForceTankFrame.Y
                               + gt.TankSpecificForceTankFrame.Z;
                }
                return checksum;
            }

            Assert.AreEqual(Run(12345), Run(12345));
        }

        [Test]
        public void ScriptedManeuver_BrakeSlowsAndAccelSpeedsTheVehicle()
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 20.0 };
            var loop = new SimulationLoop(cfg, new HoldController(), new ScriptedManeuverDriver());
            loop.BeginEpisode(7);

            double speedBeforeBrake = 0, speedAfterBrake = 0, speedAfterAccel = 0;
            while (loop.StepPhysics())
            {
                double t = loop.SimTime;
                if (t > 2.9 && t < 3.0) speedBeforeBrake = loop.VehicleState.Speed;
                if (t > 4.9 && t < 5.0) speedAfterBrake = loop.VehicleState.Speed;
                if (t > 18.9 && t < 19.0) speedAfterAccel = loop.VehicleState.Speed;
            }

            Assert.Less(speedAfterBrake, speedBeforeBrake - 2.0, "hard brake should shed speed");
            Assert.Greater(speedAfterAccel, speedAfterBrake, "full throttle should regain speed");
        }

        [Test]
        public void TankImu_AtRestCruise_ReadsApproximatelyOneGUp()
        {
            // Gentle constant cruise, no noise: the tank IMU specific force should
            // be close to (0, 0, +g) because the tank is near level and barely
            // accelerating.
            var cfg = new SimulationConfig { EpisodeSeconds = 4.0 };
            cfg.Sensors.Enabled = false;
            cfg.Vehicle.InitialSpeed = 15.0;

            var loop = new SimulationLoop(cfg, new HoldController(),
                new ConstantDriver(new DriverControls { Throttle = 0.0, Brake = 0.0 }));
            loop.BeginEpisode(1);
            while (loop.StepPhysics()) { }

            Double3 f = loop.LatestSensorBundle.TankImu.SpecificForce;
            Assert.AreEqual(0.0, f.X, 0.2);
            Assert.AreEqual(0.0, f.Y, 0.2);
            Assert.AreEqual(MathD.StandardGravity, f.Z, 0.2);
        }

        [Test]
        public void HoldController_KeepsGimbalEssentiallyStationary()
        {
            // HoldController commands zero rate; the servo setpoint stays at 0.
            // The joint deflects a little under transient external load (real
            // actuator compliance, ~1°) but must not wander: the time-averaged
            // angle stays near zero and it settles back after the maneuver.
            var loop = new SimulationLoop(ShortConfig(), new HoldController(), new ScriptedManeuverDriver());
            loop.BeginEpisode(3);
            double sumAbsPitch = 0, sumAbsRoll = 0, peak = 0;
            long n = 0;
            while (loop.StepPhysics())
            {
                var g = loop.GimbalState;
                sumAbsPitch += Math.Abs(g.Pitch.Angle);
                sumAbsRoll += Math.Abs(g.Roll.Angle);
                peak = Math.Max(peak, Math.Max(Math.Abs(g.Pitch.Angle), Math.Abs(g.Roll.Angle)));
                n++;
            }

            Assert.Less(sumAbsPitch / n, 3e-3, "mean |pitch| should be tiny");
            Assert.Less(sumAbsRoll / n, 3e-3, "mean |roll| should be tiny");
            Assert.Less(peak, 3e-2, "transient compliance under load stays small");
        }

        [Test]
        public void NoDisturbance_GimbalDoesNotMoveUnderHold()
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 5.0 };
            cfg.Sensors.Enabled = false;
            var loop = new SimulationLoop(cfg, new HoldController(),
                new ConstantDriver(DriverControls.Neutral));
            loop.BeginEpisode(1);
            while (loop.StepPhysics()) { }
            Assert.AreEqual(0.0, loop.GimbalState.Pitch.Angle, 1e-6);
            Assert.AreEqual(0.0, loop.GimbalState.Roll.Angle, 1e-6);
        }

        [Test]
        public void NullSloshModel_NeverSpills()
        {
            var loop = new SimulationLoop(new SimulationConfig { EpisodeSeconds = 20.0 },
                new HoldController(), new ScriptedManeuverDriver());
            var r = loop.RunEpisode(9);
            Assert.AreEqual(0.0, r.TotalOverflowVolume, 1e-15);
        }
    }

    public class GimbalActuatorTests
    {
        private static GimbalConfig Cfg() => new GimbalConfig();

        [Test]
        public void RateCommand_MovesJointInRightDirection_AndRespectsAngleLimit()
        {
            var cfg = Cfg();
            double dt = 1.0 / 600.0;
            var act = new GimbalActuator(cfg, dt);
            act.Reset();

            var cmd = new StabilizerCommand { PitchRateCommand = 5.0, RollRateCommand = 0.0 };
            for (int i = 0; i < 6000; i++) act.Step(cmd, 0, 0, dt); // 10 s

            Assert.Greater(act.State.Pitch.Angle, 0.0);
            Assert.LessOrEqual(act.State.Pitch.Angle, cfg.MaxAngleRad + 1e-9);
            Assert.AreEqual(cfg.MaxAngleRad, act.State.Pitch.Angle, 1e-3, "should ride the travel limit");
            Assert.IsTrue(act.State.Pitch.AtAngleLimit);
        }

        [Test]
        public void RateIsLimited_ToConfiguredMax()
        {
            var cfg = Cfg();
            double dt = 1.0 / 600.0;
            var act = new GimbalActuator(cfg, dt);
            act.Reset();

            var cmd = new StabilizerCommand { PitchRateCommand = 999.0 };
            double maxSeen = 0;
            for (int i = 0; i < 600; i++)
            {
                act.Step(cmd, 0, 0, dt);
                maxSeen = Math.Max(maxSeen, Math.Abs(act.State.Pitch.Rate));
            }
            Assert.LessOrEqual(maxSeen, cfg.MaxRateRadPerSec + 1e-6);
        }

        [Test]
        public void CommandDelay_PostponesResponse()
        {
            var cfg = new GimbalConfig { CommandDelaySeconds = 0.05 };
            double dt = 1.0 / 600.0;
            var act = new GimbalActuator(cfg, dt);
            act.Reset();

            var cmd = new StabilizerCommand { PitchRateCommand = 1.0 };
            // Within the first few steps (< 30 steps ≈ 50 ms) nothing should move.
            for (int i = 0; i < 10; i++) act.Step(cmd, 0, 0, dt);
            Assert.AreEqual(0.0, act.State.Pitch.Rate, 1e-9);

            for (int i = 0; i < 200; i++) act.Step(cmd, 0, 0, dt);
            Assert.Greater(act.State.Pitch.Rate, 0.1);
        }
    }
}
