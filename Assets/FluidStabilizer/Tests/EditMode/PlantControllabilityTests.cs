using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// Is the slosh model controllable at all by tank tilt, independent of the
    /// actuator? Drive the pendulum model directly with a time-varying lateral
    /// acceleration and a prescribed tank pitch, and compare the peak wall wave:
    ///   - tank held level (the "Hold" case)
    ///   - tank perfectly tracking the quasi-static ideal tilt (no actuator, no lag)
    ///   - tank tracking the ideal tilt through a first-order lag
    /// If perfect tracking does not help, the model or the coupling is wrong. If
    /// it helps but lagged tracking does not, the actuator dynamics are the wall.
    /// </summary>
    [Explicit("diagnostic")]
    public class PlantControllabilityTests
    {
        private const double G = MathD.StandardGravity;
        private const double Dt = 1.0 / 600.0;

        private static double PeakWave(Func<double, double> aX, Func<double, double, double> tankPitchOf,
            double seconds)
        {
            var p = new SloshParameters
            {
                TankLengthX = 1.2, TankWidthY = 0.7, TankHeightZ = 0.8,
                FillRatio = 0.625, LiquidDensity = 1000.0,
                NaturalFrequencyScale = 1.0, DampingRatio = 0.10,
                ParticipatingMassScale = 1.0, SurfaceSlopePerPendulumAngle = 1.0,
                SpillDischargeCoefficient = 0.6
            };
            var m = new PendulumSloshModel();
            m.Reset(p, new DeterministicRandom(1));

            double peak = 0, tankPitch = 0, prevPitch = 0;
            int n = (int)(seconds / Dt);
            for (int i = 0; i < n; i++)
            {
                double t = i * Dt;
                double a = aX(t);
                double targetPitch = Math.Atan2(a, G);      // ideal quasi-static tilt
                tankPitch = tankPitchOf(t, targetPitch);

                double thsDot = (tankPitch - prevPitch) / Dt;
                double thsAcc = (thsDot - _prevRate) / Dt;
                _prevRate = thsDot;
                prevPitch = tankPitch;

                var rWT = Double3x3.RotY(tankPitch);
                var input = new SloshInput
                {
                    GravityTankFrame = rWT.InverseTimes(new Double3(0, 0, -G)),
                    LinearAccelerationTankFrame = rWT.InverseTimes(new Double3(a, 0, 0)),
                    AngularVelocityTankFrame = new Double3(0, thsDot, 0),
                    AngularAccelerationTankFrame = new Double3(0, thsAcc, 0),
                    Dt = Dt
                };
                var o = m.Step(input);
                double thx = m.SampleGroundTruth().ModeState[0];
                peak = Math.Max(peak, Math.Abs(Math.Sin(thx)) * 0.6);
            }
            return peak;
        }

        private static double _prevRate;

        [Test]
        public void IdealTiltTracking_Helps_LaggedTrackingMayNot()
        {
            // A 0.35 g lateral oscillation at 0.5 Hz for 12 s.
            Func<double, double> aX = t => 3.4 * Math.Sin(2 * Math.PI * 0.5 * t);

            _prevRate = 0;
            double held = PeakWave(aX, (t, target) => 0.0, 12);
            _prevRate = 0;
            double ideal = PeakWave(aX, (t, target) => target, 12);
            _prevRate = 0;
            var lag = new LowPass(2.0); lag.Reset(0);
            double lagged = PeakWave(aX, (t, target) => lag.Step(target, Dt), 12);

            TestContext.WriteLine($"peak wall wave (m):  held={held:F3}   ideal-tilt={ideal:F3}   lagged-tilt={lagged:F3}");

            Assert.Less(ideal, held * 0.6, "perfect tank tilt tracking should roughly halve the wave");
        }
    }
}
