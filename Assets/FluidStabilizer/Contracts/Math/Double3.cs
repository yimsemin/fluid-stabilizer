using System;

namespace FluidStabilizer.Contracts
{
    /// <summary>
    /// Double-precision 3-vector used throughout the simulation core and all
    /// messages. Double precision (not <c>UnityEngine.Vector3</c>) is deliberate:
    /// the sim core must be bit-reproducible given a seed, and this type carries
    /// no engine dependency so it lives in <c>Contracts</c>.
    ///
    /// Convention: right-handed. In the World frame X=east, Y=north, Z=up.
    /// In body frames (Vehicle, Tank) X=forward, Y=left, Z=up.
    /// </summary>
    [Serializable]
    public readonly struct Double3 : IEquatable<Double3>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Double3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Double3 Zero => new Double3(0, 0, 0);
        public static Double3 UnitX => new Double3(1, 0, 0);
        public static Double3 UnitY => new Double3(0, 1, 0);
        public static Double3 UnitZ => new Double3(0, 0, 1);

        public static Double3 operator +(Double3 a, Double3 b) => new Double3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Double3 operator -(Double3 a, Double3 b) => new Double3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Double3 operator -(Double3 a) => new Double3(-a.X, -a.Y, -a.Z);
        public static Double3 operator *(Double3 a, double s) => new Double3(a.X * s, a.Y * s, a.Z * s);
        public static Double3 operator *(double s, Double3 a) => a * s;
        public static Double3 operator /(Double3 a, double s) => new Double3(a.X / s, a.Y / s, a.Z / s);

        public double SqrMagnitude => X * X + Y * Y + Z * Z;
        public double Magnitude => Math.Sqrt(SqrMagnitude);

        public Double3 Normalized
        {
            get
            {
                double m = Magnitude;
                return m > 1e-15 ? this / m : Zero;
            }
        }

        public static double Dot(Double3 a, Double3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Double3 Cross(Double3 a, Double3 b) => new Double3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public static Double3 Lerp(Double3 a, Double3 b, double t) => a + (b - a) * t;

        public bool Equals(Double3 other) =>
            X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object obj) => obj is Double3 o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        public override string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";
    }
}
