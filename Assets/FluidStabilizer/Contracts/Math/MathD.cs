using System;

namespace FluidStabilizer.Contracts
{
    /// <summary>Shared physical constants and small scalar helpers (SI units).</summary>
    public static class MathD
    {
        /// <summary>Standard gravity magnitude, m/s². World gravity is (0,0,-G).</summary>
        public const double StandardGravity = 9.80665;

        public const double Deg2Rad = Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / Math.PI;

        public static double Clamp(double v, double lo, double hi) =>
            v < lo ? lo : (v > hi ? hi : v);

        /// <summary>Clamp to a symmetric range [-limit, +limit]. <paramref name="limit"/> must be ≥ 0.</summary>
        public static double ClampAbs(double v, double limit) => Clamp(v, -limit, limit);

        /// <summary>Move <paramref name="current"/> toward <paramref name="target"/> by at most <paramref name="maxDelta"/>.</summary>
        public static double MoveToward(double current, double target, double maxDelta)
        {
            double d = target - current;
            if (Math.Abs(d) <= maxDelta) return target;
            return current + Math.Sign(d) * maxDelta;
        }

        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>Wrap an angle to (-π, π].</summary>
        public static double WrapPi(double angle)
        {
            angle %= 2.0 * Math.PI;
            if (angle <= -Math.PI) angle += 2.0 * Math.PI;
            else if (angle > Math.PI) angle -= 2.0 * Math.PI;
            return angle;
        }
    }
}
