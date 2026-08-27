using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Control;

namespace FluidStabilizer.Tests
{
    public class AxisLqrTests
    {
        [Test]
        public void Design_Converges_AndClosedLoopIsStable()
        {
            var lqr = AxisLqr.Design(omega: 4.5, dampingRatio: 0.1, controlPeriod: 0.01,
                qSloshAngle: 800, qSloshRate: 40, qGimbal: 6, rInput: 1.0);

            Assert.IsTrue(lqr.Converged, "Riccati iteration did not converge");

            // Simulate the closed loop on the design model from a disturbed state.
            double w = 4.5, w2 = w * w, zeta = 0.1;
            double ths = 0.3, thsDot = 0.0, thg = 0.0;
            double dt = 0.001;
            double peak = 0.3;
            for (int i = 0; i < 20000; i++)
            {
                double u = lqr.Control(ths, thsDot, thg);
                double thsAcc = -w2 * ths - 2 * zeta * w * thsDot + w2 * thg;
                thsDot += thsAcc * dt;
                ths += thsDot * dt;
                thg += u * dt;
                peak = Math.Max(peak, Math.Abs(ths));
            }

            Assert.Less(Math.Abs(ths), 1e-3, "closed loop did not settle the slosh angle");
            Assert.Less(Math.Abs(thg), 0.05, "gimbal did not return near zero");
            Assert.Less(peak, 0.5, "closed-loop response overshot badly");
        }

        [Test]
        public void RejectsAConstantDisturbance_ViaFeedforwardPlusGimbal()
        {
            // With a constant manoeuvre disturbance d and a feed-forward gimbal
            // bias, the slosh angle should be driven to ~0.
            var lqr = AxisLqr.Design(5.0, 0.1, 0.01, 800, 40, 6, 1.0);
            double w = 5.0, w2 = w * w, zeta = 0.1;
            double d = 2.0; // disturbance on θ̈_s
            double ffGimbal = -d / w2; // steady gimbal bias that cancels it (0 = w²θ_g + d)

            double ths = 0, thsDot = 0, thg = ffGimbal;
            double dt = 0.001;
            for (int i = 0; i < 20000; i++)
            {
                double u = lqr.Control(ths, thsDot, thg - ffGimbal);
                double acc = -w2 * ths - 2 * zeta * w * thsDot + w2 * thg + d;
                thsDot += acc * dt;
                ths += thsDot * dt;
                thg += u * dt;
            }
            Assert.Less(Math.Abs(ths), 5e-3);
        }
    }
}
