using FluidStabilizer.Contracts;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// The single place the right-handed SI simulation world meets Unity's
    /// left-handed Y-up world. Simulation: X = forward, Y = left, Z = up
    /// (right-handed). Unity: X = right, Y = up, Z = forward (left-handed).
    /// Mapping: unity = (−si.y, si.z, si.x) — a permutation with one sign flip,
    /// determinant −1, which is exactly the handedness flip we need.
    ///
    /// Nothing in <c>FluidStabilizer.Core/*</c> ever touches a Unity type; all
    /// conversion is here.
    /// </summary>
    public static class Frames
    {
        public static Vector3 ToUnity(Double3 v)
            => new Vector3((float)(-v.Y), (float)v.Z, (float)v.X);

        public static Double3 ToSim(Vector3 v)
            => new Double3(v.z, -v.x, v.y);

        /// <summary>
        /// Convert a right-handed SI rotation matrix (columns = body axes in the
        /// parent frame) into the equivalent Unity quaternion.
        /// </summary>
        public static Quaternion ToUnity(Double3x3 rParentFromBody)
        {
            // Body basis vectors expressed in the SI parent frame.
            var bx = new Double3(rParentFromBody.R00, rParentFromBody.R10, rParentFromBody.R20);
            var by = new Double3(rParentFromBody.R01, rParentFromBody.R11, rParentFromBody.R21);
            var bz = new Double3(rParentFromBody.R02, rParentFromBody.R12, rParentFromBody.R22);

            // In Unity: the body's forward is SI +X, up is SI +Z.
            Vector3 forward = ToUnity(bx);
            Vector3 up = ToUnity(bz);
            return Quaternion.LookRotation(forward, up);
        }
    }
}
