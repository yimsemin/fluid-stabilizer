using System;

namespace FluidStabilizer.Contracts
{
    /// <summary>
    /// A 3×3 matrix, used for rotation matrices between the World, Vehicle and
    /// Tank frames. Row-major. Double precision for the same determinism reason
    /// as <see cref="Double3"/>.
    /// </summary>
    [Serializable]
    public readonly struct Double3x3
    {
        // r{row}{col}
        public readonly double R00, R01, R02;
        public readonly double R10, R11, R12;
        public readonly double R20, R21, R22;

        public Double3x3(
            double r00, double r01, double r02,
            double r10, double r11, double r12,
            double r20, double r21, double r22)
        {
            R00 = r00; R01 = r01; R02 = r02;
            R10 = r10; R11 = r11; R12 = r12;
            R20 = r20; R21 = r21; R22 = r22;
        }

        public static Double3x3 Identity => new Double3x3(
            1, 0, 0,
            0, 1, 0,
            0, 0, 1);

        /// <summary>Rotation about X (roll), right-handed, angle in radians.</summary>
        public static Double3x3 RotX(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Double3x3(
                1, 0, 0,
                0, c, -s,
                0, s, c);
        }

        /// <summary>Rotation about Y (pitch), right-handed, angle in radians.</summary>
        public static Double3x3 RotY(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Double3x3(
                c, 0, s,
                0, 1, 0,
                -s, 0, c);
        }

        /// <summary>Rotation about Z (yaw), right-handed, angle in radians.</summary>
        public static Double3x3 RotZ(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Double3x3(
                c, -s, 0,
                s, c, 0,
                0, 0, 1);
        }

        /// <summary>
        /// Body orientation from yaw→pitch→roll applied as Rz(yaw)·Ry(pitch)·Rx(roll).
        /// Maps body-frame vectors into the parent frame.
        /// </summary>
        public static Double3x3 FromYawPitchRoll(double yaw, double pitch, double roll)
            => RotZ(yaw) * RotY(pitch) * RotX(roll);

        /// <summary>
        /// The stabilizer gimbal: pitch about the parent Y, then roll about the
        /// once-pitched X. R_VT = Ry(pitch)·Rx(roll) maps Tank-frame vectors into
        /// the Vehicle frame. (See docs/frames-units-conventions.md.)
        /// </summary>
        public static Double3x3 Gimbal(double pitch, double roll)
            => RotY(pitch) * RotX(roll);

        public static Double3x3 operator +(Double3x3 a, Double3x3 b) => new Double3x3(
            a.R00 + b.R00, a.R01 + b.R01, a.R02 + b.R02,
            a.R10 + b.R10, a.R11 + b.R11, a.R12 + b.R12,
            a.R20 + b.R20, a.R21 + b.R21, a.R22 + b.R22);

        public static Double3x3 operator -(Double3x3 a, Double3x3 b) => new Double3x3(
            a.R00 - b.R00, a.R01 - b.R01, a.R02 - b.R02,
            a.R10 - b.R10, a.R11 - b.R11, a.R12 - b.R12,
            a.R20 - b.R20, a.R21 - b.R21, a.R22 - b.R22);

        public static Double3x3 operator *(Double3x3 a, double s) => new Double3x3(
            a.R00 * s, a.R01 * s, a.R02 * s,
            a.R10 * s, a.R11 * s, a.R12 * s,
            a.R20 * s, a.R21 * s, a.R22 * s);

        public static Double3x3 operator *(double s, Double3x3 a) => a * s;

        public static Double3x3 operator *(Double3x3 a, Double3x3 b) => new Double3x3(
            a.R00 * b.R00 + a.R01 * b.R10 + a.R02 * b.R20,
            a.R00 * b.R01 + a.R01 * b.R11 + a.R02 * b.R21,
            a.R00 * b.R02 + a.R01 * b.R12 + a.R02 * b.R22,

            a.R10 * b.R00 + a.R11 * b.R10 + a.R12 * b.R20,
            a.R10 * b.R01 + a.R11 * b.R11 + a.R12 * b.R21,
            a.R10 * b.R02 + a.R11 * b.R12 + a.R12 * b.R22,

            a.R20 * b.R00 + a.R21 * b.R10 + a.R22 * b.R20,
            a.R20 * b.R01 + a.R21 * b.R11 + a.R22 * b.R21,
            a.R20 * b.R02 + a.R21 * b.R12 + a.R22 * b.R22);

        /// <summary>Transform a vector: parent = R · child.</summary>
        public static Double3 operator *(Double3x3 m, Double3 v) => new Double3(
            m.R00 * v.X + m.R01 * v.Y + m.R02 * v.Z,
            m.R10 * v.X + m.R11 * v.Y + m.R12 * v.Z,
            m.R20 * v.X + m.R21 * v.Y + m.R22 * v.Z);

        /// <summary>Transpose. For a rotation matrix this is also the inverse.</summary>
        public Double3x3 Transpose() => new Double3x3(
            R00, R10, R20,
            R01, R11, R21,
            R02, R12, R22);

        /// <summary>Inverse-transform a vector: child = Rᵀ · parent.</summary>
        public Double3 InverseTimes(Double3 v) => new Double3(
            R00 * v.X + R10 * v.Y + R20 * v.Z,
            R01 * v.X + R11 * v.Y + R21 * v.Z,
            R02 * v.X + R12 * v.Y + R22 * v.Z);
    }
}
