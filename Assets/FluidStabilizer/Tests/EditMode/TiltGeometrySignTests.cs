using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// Pins the sign of the tank-tilt → slosh coupling against the real plant.
    /// Holds the pendulum model under a constant lateral acceleration with the
    /// tank rotated to a fixed pitch, and checks which direction of tilt reduces
    /// the steady pendulum angle. This is the sign the baseline controllers must
    /// use.
    /// </summary>
    public class TiltGeometrySignTests
    {
        private const double G = MathD.StandardGravity;
        private const double Dt = 1.0 / 600.0;

        private static double SteadyPendulumAngle(double aX, double tankPitch)
        {
            var p = new SloshParameters
            {
                TankLengthX = 1.2, TankWidthY = 0.7, TankHeightZ = 0.8,
                FillRatio = 0.5, LiquidDensity = 1000.0,
                NaturalFrequencyScale = 1.0, DampingRatio = 0.3,
                ParticipatingMassScale = 1.0, SurfaceSlopePerPendulumAngle = 1.0,
                SpillDischargeCoefficient = 0.6
            };
            var m = new PendulumSloshModel();
            m.Reset(p, new DeterministicRandom(1));

            // Tank rotated by tankPitch about Y: gravity in the tank frame, and
            // the vehicle acceleration (aX forward, world) expressed in the tank
            // frame.
            var rWT = Double3x3.RotY(tankPitch); // vehicle≈world here
            Double3 gTank = rWT.InverseTimes(new Double3(0, 0, -G));
            Double3 aTank = rWT.InverseTimes(new Double3(aX, 0, 0));

            var input = new SloshInput
            {
                GravityTankFrame = gTank,
                LinearAccelerationTankFrame = aTank,
                AngularVelocityTankFrame = Double3.Zero,
                AngularAccelerationTankFrame = Double3.Zero,
                Dt = Dt
            };
            for (int i = 0; i < 4000; i++) m.Step(input);
            return m.SampleGroundTruth().ModeState[0];
        }

        [Test]
        public void TiltingWithTheAcceleration_ReducesTheSteadyPendulumAngle()
        {
            const double aX = 3.0; // forward acceleration
            double noTilt = SteadyPendulumAngle(aX, 0.0);

            // The felt specific force is (aX, 0, g); TiltGeometry.DesiredPitch
            // returns Sign * atan2(aX, g).
            double desired = FluidStabilizer.Controller.TiltGeometry.DesiredPitch(new Double3(aX, 0, G));
            double withTilt = SteadyPendulumAngle(aX, desired);

            TestContext.WriteLine($"aX={aX}: |θ| no-tilt={Math.Abs(noTilt):F4}, " +
                                  $"desiredPitch={desired:F4}, |θ| with-tilt={Math.Abs(withTilt):F4}");

            Assert.Less(Math.Abs(withTilt), Math.Abs(noTilt) * 0.35,
                "TiltGeometry.Sign is wrong — tilting to the 'desired' angle should nearly null the pendulum");
        }

        [Test]
        public void OppositeTilt_MakesItWorse()
        {
            const double aX = 3.0;
            double noTilt = Math.Abs(SteadyPendulumAngle(aX, 0.0));
            double wrongWay = Math.Abs(SteadyPendulumAngle(aX,
                -FluidStabilizer.Controller.TiltGeometry.DesiredPitch(new Double3(aX, 0, G))));
            Assert.Greater(wrongWay, noTilt, "tilting the wrong way should increase the pendulum angle");
        }
    }
}
