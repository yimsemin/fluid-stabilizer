using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Control
{
    /// <summary>
    /// Infinite-horizon discrete LQR gain for one stabilizer axis.
    ///
    /// Design model (continuous), state x = [θ_slosh, θ̇_slosh, θ_gimbal]ᵀ,
    /// input u = gimbal rate command:
    ///
    ///   θ̇_s = θ̇_s
    ///   θ̈_s = −ω²·θ_s − 2ζω·θ̇_s + ω²·θ_g      (manoeuvre enters as a disturbance)
    ///   θ̇_g = u                                (servo treated as an ideal rate follower)
    ///
    /// Discretised at the control period; the discrete Riccati recursion is
    /// iterated to convergence. Because B = [0,0,1]ᵀ, R + BᵀPB is a scalar and no
    /// matrix inverse is needed. Result: K = [k₀,k₁,k₂], u = −K·x.
    /// </summary>
    public readonly struct AxisLqr
    {
        public readonly double K0, K1, K2;
        public readonly bool Converged;

        private AxisLqr(double k0, double k1, double k2, bool converged)
        {
            K0 = k0; K1 = k1; K2 = k2; Converged = converged;
        }

        public double Control(double sloshAngle, double sloshRate, double gimbalError)
            => -(K0 * sloshAngle + K1 * sloshRate + K2 * gimbalError);

        public static AxisLqr Design(
            double omega, double dampingRatio, double controlPeriod,
            double qSloshAngle, double qSloshRate, double qGimbal, double rInput)
        {
            double w2 = omega * omega;
            double ts = controlPeriod;

            var ac = new Double3x3(
                0, 1, 0,
                -w2, -2.0 * dampingRatio * omega, w2,
                0, 0, 0);

            // Matrix exponential and its integral by Taylor series (ω·Ts ≪ 1).
            Double3x3 ac2 = ac * ac, ac3 = ac2 * ac;
            Double3x3 ad = Double3x3.Identity + ac * ts + ac2 * (0.5 * ts * ts) + ac3 * (ts * ts * ts / 6.0);
            Double3x3 gd = Double3x3.Identity * ts + ac * (0.5 * ts * ts) + ac2 * (ts * ts * ts / 6.0)
                           + ac3 * (ts * ts * ts * ts / 24.0);
            var bd = new Double3(gd.R02, gd.R12, gd.R22); // Gd · [0,0,1]ᵀ

            var q = new Double3x3(
                qSloshAngle, 0, 0,
                0, qSloshRate, 0,
                0, 0, qGimbal);
            Double3x3 adT = ad.Transpose();

            var p = q;
            bool converged = false;
            for (int iter = 0; iter < 5000; iter++)
            {
                Double3 pb = Mul(p, bd);                 // P·B
                double s = rInput + Double3.Dot(bd, pb); // R + BᵀPB
                Double3 w = Mul(adT, pb);                // AᵀPB  ( = (BᵀPA)ᵀ, P symmetric )

                Double3x3 pNext = q + adT * p * ad - Outer(w, w) * (1.0 / s);

                if (MaxAbsDiff(pNext, p) < 1e-12) { p = pNext; converged = true; break; }
                p = pNext;
            }

            Double3 pbF = Mul(p, bd);
            double sF = rInput + Double3.Dot(bd, pbF);
            Double3 k = Mul(adT, pbF) * (1.0 / sF);      // K = (R+BᵀPB)⁻¹ BᵀPA

            return new AxisLqr(k.X, k.Y, k.Z, converged);
        }

        private static Double3 Mul(Double3x3 m, Double3 v) => new Double3(
            m.R00 * v.X + m.R01 * v.Y + m.R02 * v.Z,
            m.R10 * v.X + m.R11 * v.Y + m.R12 * v.Z,
            m.R20 * v.X + m.R21 * v.Y + m.R22 * v.Z);

        private static Double3x3 Outer(Double3 a, Double3 b) => new Double3x3(
            a.X * b.X, a.X * b.Y, a.X * b.Z,
            a.Y * b.X, a.Y * b.Y, a.Y * b.Z,
            a.Z * b.X, a.Z * b.Y, a.Z * b.Z);

        private static double MaxAbsDiff(Double3x3 a, Double3x3 b)
        {
            double m = 0;
            foreach (double d in new[]
            {
                a.R00 - b.R00, a.R01 - b.R01, a.R02 - b.R02,
                a.R10 - b.R10, a.R11 - b.R11, a.R12 - b.R12,
                a.R20 - b.R20, a.R21 - b.R21, a.R22 - b.R22
            }) m = Math.Max(m, Math.Abs(d));
            return m;
        }
    }
}
