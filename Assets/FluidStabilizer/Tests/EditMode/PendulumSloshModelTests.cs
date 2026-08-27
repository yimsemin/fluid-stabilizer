using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    public class PendulumSloshModelTests
    {
        private const double G = MathD.StandardGravity;
        private const double Dt = 1.0 / 600.0;

        private static SloshParameters Params(double fill = 0.5, double zeta = 0.05) => new SloshParameters
        {
            TankLengthX = 1.2,
            TankWidthY = 0.7,
            TankHeightZ = 0.6,
            FillRatio = fill,
            LiquidDensity = 1000.0,
            NaturalFrequencyScale = 1.0,
            DampingRatio = zeta,
            ParticipatingMassScale = 1.0,
            SurfaceSlopePerPendulumAngle = 1.0,
            SpillDischargeCoefficient = 0.6
        };

        private static SloshInput Input(Double3 accelTank, double dt = Dt) => new SloshInput
        {
            GravityTankFrame = new Double3(0, 0, -G),
            LinearAccelerationTankFrame = accelTank,
            AngularVelocityTankFrame = Double3.Zero,
            AngularAccelerationTankFrame = Double3.Zero,
            Dt = dt
        };

        private static PendulumSloshModel Fresh(SloshParameters p)
        {
            var m = new PendulumSloshModel();
            m.Reset(p, new DeterministicRandom(1));
            return m;
        }

        [Test]
        public void AtRest_ReactionIsLiquidWeightDownward_NoSpill()
        {
            var p = Params();
            var m = Fresh(p);
            SloshOutput o = default;
            for (int i = 0; i < 600; i++) o = m.Step(Input(Double3.Zero));

            double h = p.FillRatio * p.TankHeightZ;
            double liquidMass = p.LiquidDensity * p.TankLengthX * p.TankWidthY * h;

            Assert.AreEqual(0.0, o.ReactionForceTankFrame.X, 1e-6);
            Assert.AreEqual(0.0, o.ReactionForceTankFrame.Y, 1e-6);
            Assert.AreEqual(-liquidMass * G, o.ReactionForceTankFrame.Z, liquidMass * G * 1e-3);
            Assert.AreEqual(0.0, o.OverflowVolumeThisStep, 1e-12);
        }

        [Test]
        public void ForwardAcceleration_PushesPitchPendulumBackward()
        {
            var m = Fresh(Params(zeta: 0.2));
            for (int i = 0; i < 1200; i++) m.Step(Input(new Double3(3.0, 0, 0))); // +X accel, settle

            double thx = m.SampleGroundTruth().ModeState[0];
            Assert.Less(thx, -0.05, "liquid should slosh toward -X under +X acceleration");
            // small-angle equilibrium ≈ -a/g
            Assert.AreEqual(-3.0 / G, thx, 0.08);
        }

        [Test]
        public void LeftAcceleration_PushesRollPendulumRight()
        {
            var m = Fresh(Params(zeta: 0.2));
            for (int i = 0; i < 1200; i++) m.Step(Input(new Double3(0, 2.5, 0)));

            double thy = m.SampleGroundTruth().ModeState[2];
            Assert.Less(thy, -0.03, "liquid should slosh toward -Y under +Y acceleration");
        }

        [Test]
        public void RingDownFrequency_MatchesRectangularTankFirstMode()
        {
            var p = Params(fill: 0.5, zeta: 0.0);
            var m = Fresh(p);

            // Excite with a step of lateral accel, then release and count zero crossings.
            for (int i = 0; i < 400; i++) m.Step(Input(new Double3(2.0, 0, 0)));

            double prev = m.SampleGroundTruth().ModeState[0];
            int crossings = 0;
            double firstCrossT = -1, lastCrossT = -1;
            int steps = 6000; // 10 s
            for (int i = 0; i < steps; i++)
            {
                m.Step(Input(Double3.Zero));
                double th = m.SampleGroundTruth().ModeState[0];
                // equilibrium after release is 0; detect sign changes of th
                if (Math.Sign(th) != Math.Sign(prev) && prev != 0)
                {
                    crossings++;
                    double t = i * Dt;
                    if (firstCrossT < 0) firstCrossT = t;
                    lastCrossT = t;
                }
                prev = th;
            }

            Assert.Greater(crossings, 4, "should oscillate several times with zero damping");
            double halfPeriods = crossings - 1;
            double measuredPeriod = 2.0 * (lastCrossT - firstCrossT) / halfPeriods;

            double h = p.FillRatio * p.TankHeightZ;
            double k = Math.PI / p.TankLengthX;
            double omega = Math.Sqrt(G * k * Math.Tanh(k * h));
            double expectedPeriod = 2.0 * Math.PI / omega;

            Assert.AreEqual(expectedPeriod, measuredPeriod, expectedPeriod * 0.15,
                $"measured {measuredPeriod:F3}s vs expected {expectedPeriod:F3}s");
        }

        [Test]
        public void Damping_DecaysTheOscillation()
        {
            var m = Fresh(Params(fill: 0.5, zeta: 0.08));
            for (int i = 0; i < 400; i++) m.Step(Input(new Double3(3.0, 0, 0)));

            double ampEarly = 0, ampLate = 0;
            for (int i = 0; i < 1200; i++) // first ~2 s after release
            {
                m.Step(Input(Double3.Zero));
                ampEarly = Math.Max(ampEarly, Math.Abs(m.SampleGroundTruth().ModeState[0]));
            }
            for (int i = 0; i < 3000; i++) m.Step(Input(Double3.Zero)); // skip ahead
            for (int i = 0; i < 1200; i++)
            {
                m.Step(Input(Double3.Zero));
                ampLate = Math.Max(ampLate, Math.Abs(m.SampleGroundTruth().ModeState[0]));
            }

            Assert.Less(ampLate, ampEarly * 0.6, "oscillation should decay with damping");
        }

        [Test]
        public void SloshKineticEnergy_IsPositiveWhileSloshing_AndZeroAtRest()
        {
            var m = Fresh(Params());
            Assert.AreEqual(0.0, m.SampleGroundTruth().SloshKineticEnergy, 1e-12);

            double maxKe = 0;
            for (int i = 0; i < 1200; i++)
            {
                m.Step(Input(new Double3(i < 600 ? 4.0 : 0.0, 0, 0)));
                maxKe = Math.Max(maxKe, m.SampleGroundTruth().SloshKineticEnergy);
            }
            Assert.Greater(maxKe, 1.0);
        }

        [Test]
        public void AggressiveSlosh_WithLowFreeboard_Spills_ButCalmDoesNot()
        {
            // Low freeboard: fill 0.9 of a 0.6 m tank ⇒ 6 cm freeboard.
            var p = Params(fill: 0.9, zeta: 0.02);

            var calm = Fresh(p);
            double calmSpill = 0;
            for (int i = 0; i < 3000; i++) calmSpill += calm.Step(Input(Double3.Zero)).OverflowVolumeThisStep;
            Assert.AreEqual(0.0, calmSpill, 1e-9);

            var rough = Fresh(p);
            double roughSpill = 0;
            for (int i = 0; i < 6000; i++)
            {
                double a = 6.0 * Math.Sin(2.0 * Math.PI * 0.7 * i * Dt); // resonant-ish shaking
                roughSpill += rough.Step(Input(new Double3(a, 0, 0))).OverflowVolumeThisStep;
            }
            Assert.Greater(roughSpill, 0.0, "hard resonant shaking with 6 cm freeboard should spill");
        }

        [Test]
        public void CoupledIntoLoop_IsDeterministic_AndCanSpillUnderHold()
        {
            SimulationConfig Cfg()
            {
                var c = new SimulationConfig { EpisodeSeconds = 20.0 };
                c.Slosh.FillRatio = 0.85;
                c.Slosh.DampingRatio = 0.03;
                return c;
            }

            EpisodeResult Run(ulong seed) => new SimulationLoop(
                Cfg(), new HoldController(), new ScriptedManeuverDriver(),
                new PendulumSloshModel()).RunEpisode(seed);

            var a = Run(42);
            var b = Run(42);
            Assert.AreEqual(a.TotalOverflowVolume, b.TotalOverflowVolume, 1e-12);
            Assert.AreEqual(a.SloshEnergyIntegral, b.SloshEnergyIntegral, 1e-12);
            Assert.AreEqual(a.WorstFreeboard, b.WorstFreeboard, 1e-12);

            Assert.Greater(a.SloshEnergyIntegral, 0.0, "the scripted maneuver should excite slosh");
        }

        [Test]
        public void CoupledIntoLoop_StaysBounded_NoBlowUp()
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 40.0 };
            var loop = new SimulationLoop(cfg, new HoldController(), new ScriptedManeuverDriver(),
                new PendulumSloshModel());
            loop.BeginEpisode(1);
            while (loop.StepPhysics())
            {
                var gt = loop.LatestGroundTruth;
                Assert.IsFalse(double.IsNaN(gt.PitchAngle) || double.IsInfinity(gt.PitchAngle));
                Assert.Less(Math.Abs(gt.RollTorque), 1e6, "actuator load should not diverge");
                Assert.Less(gt.SloshKineticEnergy, 1e6);
            }
        }
    }
}
