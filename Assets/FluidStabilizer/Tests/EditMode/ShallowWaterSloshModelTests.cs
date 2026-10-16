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
    public class ShallowWaterSloshModelTests
    {
        private const double G = MathD.StandardGravity;
        private const double Dt = 1.0 / 600.0;

        private static SloshParameters Params(double fill = 0.5, double zeta = 0.05,
            double quad = 0.0) => new SloshParameters
        {
            TankLengthX = 1.2,
            TankWidthY = 0.7,
            TankHeightZ = 0.6,
            FillRatio = fill,
            LiquidDensity = 1000.0,
            NaturalFrequencyScale = 1.0,
            DampingRatio = zeta,
            QuadraticDamping = quad,
            ParticipatingMassScale = 1.0,
            SurfaceSlopePerPendulumAngle = 1.0,
            SpillDischargeCoefficient = 0.6
        };

        private static SloshInput Input(Double3 accelTank) => new SloshInput
        {
            GravityTankFrame = new Double3(0, 0, -G),
            LinearAccelerationTankFrame = accelTank,
            AngularVelocityTankFrame = Double3.Zero,
            AngularAccelerationTankFrame = Double3.Zero,
            Dt = Dt
        };

        private static ShallowWaterSloshModel Fresh(SloshParameters p)
        {
            var m = new ShallowWaterSloshModel();
            m.Reset(p, new DeterministicRandom(1));
            return m;
        }

        [Test]
        public void GridSize_MatchesNominalTank()
        {
            var m = Fresh(Params());
            var (nx, ny) = m.GridSize;
            Assert.AreEqual(48, nx);
            Assert.AreEqual(28, ny);
        }

        [Test]
        public void AtRest_StaysFlat_WeightReaction_NoSpill()
        {
            var p = Params();
            var m = Fresh(p);
            SloshOutput o = default;
            for (int i = 0; i < 1200; i++) o = m.Step(Input(Double3.Zero));

            double h = p.FillRatio * p.TankHeightZ;
            double liquidMass = p.LiquidDensity * p.TankLengthX * p.TankWidthY * h;

            Assert.AreEqual(0.0, o.ReactionForceTankFrame.X, liquidMass * G * 1e-6);
            Assert.AreEqual(0.0, o.ReactionForceTankFrame.Y, liquidMass * G * 1e-6);
            Assert.AreEqual(-liquidMass * G, o.ReactionForceTankFrame.Z, liquidMass * G * 1e-4);
            Assert.AreEqual(0.0, o.OverflowVolumeThisStep, 1e-12);
            Assert.Greater(o.MinFreeboard, 0.0);

            // lake-at-rest preserved exactly: the surface has not rippled
            var (nx, ny) = m.GridSize;
            for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
                Assert.AreEqual(h, m.SurfaceHeight(i, j), 1e-9, $"cell ({i},{j}) drifted");
        }

        [Test]
        public void ForwardAcceleration_TiltsSurfaceBackward()
        {
            var m = Fresh(Params(zeta: 0.25, quad: 0.5));
            for (int i = 0; i < 3000; i++) m.Step(Input(new Double3(2.0, 0, 0))); // +X, settle

            double thx = m.SampleGroundTruth().ModeState[0];
            Assert.Less(thx, -0.03, "surface should tilt so liquid piles toward -X");
            // quasi-static surface slope ≈ -a/g
            Assert.AreEqual(-2.0 / G, thx, 0.06);
        }

        [Test]
        public void LeftAcceleration_TiltsSurfaceRight()
        {
            var m = Fresh(Params(zeta: 0.25, quad: 0.5));
            for (int i = 0; i < 3000; i++) m.Step(Input(new Double3(0, 1.8, 0)));

            double thy = m.SampleGroundTruth().ModeState[2];
            Assert.Less(thy, -0.02, "surface should tilt so liquid piles toward -Y");
        }

        [Test]
        public void MassIsConserved_FieldPlusSpill()
        {
            // Volume in the grid plus everything that has spilled over the rim
            // must equal the starting volume: the scheme neither creates nor
            // destroys liquid, it only advects it or lets it leave over the weir.
            var p = Params(fill: 0.45, zeta: 0.02, quad: 0.4);
            var m = Fresh(p);
            double v0 = m.LiquidVolume;
            double spilled = 0.0;
            for (int i = 0; i < 4000; i++)
            {
                double a = 3.5 * Math.Sin(2.0 * Math.PI * 0.62 * i * Dt);
                spilled += m.Step(Input(new Double3(a, 0.7 * a, 0))).OverflowVolumeThisStep;
            }
            // tolerance covers the O(1e-6 m) positivity floor applied per cell
            Assert.AreEqual(v0, m.LiquidVolume + spilled, v0 * 1e-4,
                "grid volume + cumulative spill should equal the initial volume");
        }

        [Test]
        public void RingDownFrequency_MatchesRectangularTankFirstMode()
        {
            var p = Params(fill: 0.5, zeta: 0.0);
            var m = Fresh(p);

            for (int i = 0; i < 500; i++) m.Step(Input(new Double3(2.5, 0, 0))); // excite

            double prev = m.SampleGroundTruth().ModeState[0];
            int crossings = 0;
            double firstT = -1, lastT = -1;
            for (int i = 0; i < 7200; i++) // 12 s ring-down
            {
                m.Step(Input(Double3.Zero));
                double th = m.SampleGroundTruth().ModeState[0];
                if (Math.Sign(th) != Math.Sign(prev) && prev != 0)
                {
                    crossings++;
                    double t = i * Dt;
                    if (firstT < 0) firstT = t;
                    lastT = t;
                }
                prev = th;
            }

            Assert.Greater(crossings, 4, "should oscillate several times");
            double measuredPeriod = 2.0 * (lastT - firstT) / (crossings - 1);

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
            var m = Fresh(Params(fill: 0.5, zeta: 0.10));
            for (int i = 0; i < 500; i++) m.Step(Input(new Double3(3.0, 0, 0)));

            double ampEarly = 0, ampLate = 0;
            for (int i = 0; i < 1200; i++)
            {
                m.Step(Input(Double3.Zero));
                ampEarly = Math.Max(ampEarly, Math.Abs(m.SampleGroundTruth().ModeState[0]));
            }
            for (int i = 0; i < 3000; i++) m.Step(Input(Double3.Zero));
            for (int i = 0; i < 1200; i++)
            {
                m.Step(Input(Double3.Zero));
                ampLate = Math.Max(ampLate, Math.Abs(m.SampleGroundTruth().ModeState[0]));
            }

            Assert.Less(ampLate, ampEarly * 0.6, "oscillation should decay");
        }

        [Test]
        public void KineticEnergy_PositiveWhileSloshing_ZeroAtRest()
        {
            var m = Fresh(Params(zeta: 0.05));
            Assert.AreEqual(0.0, m.SampleGroundTruth().SloshKineticEnergy, 1e-9);

            double maxKe = 0;
            for (int i = 0; i < 1800; i++)
            {
                m.Step(Input(new Double3(i < 600 ? 4.0 : 0.0, 0, 0)));
                maxKe = Math.Max(maxKe, m.SampleGroundTruth().SloshKineticEnergy);
            }
            Assert.Greater(maxKe, 1.0);
        }

        [Test]
        public void ResonantShake_LowFreeboard_Spills_ButCalmDoesNot()
        {
            var p = Params(fill: 0.9, zeta: 0.02, quad: 0.5); // 6 cm freeboard

            var calm = Fresh(p);
            double calmSpill = 0;
            for (int i = 0; i < 3000; i++) calmSpill += calm.Step(Input(Double3.Zero)).OverflowVolumeThisStep;
            Assert.AreEqual(0.0, calmSpill, 1e-9);

            var rough = Fresh(p);
            double roughSpill = 0;
            for (int i = 0; i < 6000; i++)
            {
                double a = 6.0 * Math.Sin(2.0 * Math.PI * 0.75 * i * Dt);
                roughSpill += rough.Step(Input(new Double3(a, 0, 0))).OverflowVolumeThisStep;
            }
            Assert.Greater(roughSpill, 0.0, "hard resonant shaking at 6 cm freeboard should spill");
        }

        [Test]
        public void IsDeterministic_Standalone()
        {
            double Run()
            {
                var m = Fresh(Params(zeta: 0.03));
                double acc = 0;
                for (int i = 0; i < 3000; i++)
                {
                    double a = 4.0 * Math.Sin(2.0 * Math.PI * 0.55 * i * Dt);
                    var o = m.Step(Input(new Double3(a, 0.5 * a, 0)));
                    acc += o.ReactionTorqueTankFrame.Y + o.ReactionForceTankFrame.X + o.OverflowVolumeThisStep;
                }
                return acc;
            }

            Assert.AreEqual(Run(), Run(), 0.0, "standalone stepping must be bit-identical");
        }

        [Test]
        public void CoupledIntoLoop_IsDeterministic_AndExcitesSlosh()
        {
            SimulationConfig Cfg()
            {
                var c = new SimulationConfig { EpisodeSeconds = 18.0 };
                c.Slosh.FillRatio = 0.8;
                c.Slosh.DampingRatio = 0.04;
                return c;
            }

            EpisodeResult Run(ulong seed) => new SimulationLoop(
                Cfg(), new HoldController(), new ScriptedManeuverDriver(),
                new ShallowWaterSloshModel()).RunEpisode(seed);

            var a = Run(42);
            var b = Run(42);
            Assert.AreEqual(a.TotalOverflowVolume, b.TotalOverflowVolume, 1e-12);
            Assert.AreEqual(a.SloshEnergyIntegral, b.SloshEnergyIntegral, 1e-12);
            Assert.AreEqual(a.WorstFreeboard, b.WorstFreeboard, 1e-12);
            Assert.Greater(a.SloshEnergyIntegral, 0.0);
        }

        [Test]
        public void CoupledIntoLoop_StaysBounded_NoBlowUp()
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 40.0 };
            var loop = new SimulationLoop(cfg, new HoldController(), new ScriptedManeuverDriver(),
                new ShallowWaterSloshModel());
            loop.BeginEpisode(1);
            while (loop.StepPhysics())
            {
                var gt = loop.LatestGroundTruth;
                Assert.IsFalse(double.IsNaN(gt.PitchAngle) || double.IsInfinity(gt.PitchAngle));
                Assert.Less(Math.Abs(gt.RollTorque), 1e6, "actuator load should not diverge");
                Assert.Less(gt.SloshKineticEnergy, 1e6);
            }
        }

        [Test]
        public void LowAmplitude_SettlingAgreesWithPendulumModel()
        {
            // Under a gentle sustained acceleration both models should settle to
            // about the same quasi-static surface tilt (≈ -a/g).
            var pp = Params(zeta: 0.2, quad: 0.3);

            var sw = Fresh(pp);
            var pend = new PendulumSloshModel();
            pend.Reset(pp, new DeterministicRandom(1));

            for (int i = 0; i < 4000; i++)
            {
                sw.Step(Input(new Double3(1.2, 0, 0)));
                pend.Step(Input(new Double3(1.2, 0, 0)));
            }

            double thSw = sw.SampleGroundTruth().ModeState[0];
            double thPend = pend.SampleGroundTruth().ModeState[0];
            Assert.AreEqual(-1.2 / G, thSw, 0.05);
            Assert.AreEqual(thPend, thSw, 0.06, "low-amplitude tilt should roughly match the pendulum ROM");
        }

        private static (double ke, double volume) PulseThenRelease(SloshParameters p, Double3 pulse)
        {
            var m = Fresh(p);
            for (int i = 0; i < 300; i++) m.Step(Input(pulse));
            for (int i = 0; i < 1800; i++) m.Step(Input(Double3.Zero));
            return (m.SampleGroundTruth().SloshKineticEnergy, m.LiquidVolume);
        }

        [Test]
        public void Baffles_ZeroLoss_ChangesNothing()
        {
            var plain = PulseThenRelease(Params(0.4), new Double3(3, 0, 0));
            var p = Params(0.4); p.BaffleCountX = 2; p.BaffleLossCoeff = 0.0;
            var zero = PulseThenRelease(p, new Double3(3, 0, 0));
            Assert.AreEqual(plain.ke, zero.ke, 1e-12);
        }

        [Test]
        public void Baffles_DampLongitudinalSlosh_AndConserveVolume()
        {
            var plain = PulseThenRelease(Params(0.4), new Double3(3, 0, 0));
            var p = Params(0.4); p.BaffleCountX = 2; p.BaffleLossCoeff = 20.0;
            var baf = PulseThenRelease(p, new Double3(3, 0, 0));
            Assert.Less(baf.ke, 0.5 * plain.ke, $"baffled {baf.ke:E3} vs plain {plain.ke:E3}");
            Assert.AreEqual(plain.volume, baf.volume, plain.volume * 1e-9);
        }

        [Test]
        public void XBaffles_DoNotDampLateralSlosh()
        {
            var plain = PulseThenRelease(Params(0.4), new Double3(0, 3, 0));
            var p = Params(0.4); p.BaffleCountX = 2; p.BaffleLossCoeff = 20.0;
            var baf = PulseThenRelease(p, new Double3(0, 3, 0));
            Assert.AreEqual(plain.ke, baf.ke, plain.ke * 0.05);
        }
    }
}
