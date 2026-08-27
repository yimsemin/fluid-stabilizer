using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// Shared geometry for the baseline controllers: the pitch/roll angles that
    /// would make the tank floor perpendicular to a given specific-force vector
    /// measured in the tank frame.
    ///
    /// A perfectly level tank at rest reads specific force (0, 0, +g). If the
    /// vector has an X or Y component, rotating the tank by these angles brings
    /// it back onto +Z — i.e. aligns the liquid surface with the felt "down".
    /// </summary>
    public static class TiltGeometry
    {
        // Gimbal convention R_VT = Ry(pitch)·Rx(roll):
        //   +pitch tilts the tank up-axis toward vehicle +X (raises the rear rim),
        //   +roll  tilts the tank up-axis toward vehicle −Y (raises the right rim).
        // Under +X acceleration the specific force reads (+aₓ, 0, g) and the
        // liquid pools at the rear; under +Y acceleration it reads (0, +a_y, g)
        // and the liquid pools to the right. Both corrections are therefore the
        // same sign as the corresponding specific-force component.

        /// <summary>Desired pitch (rad) to bring the specific force onto +Z_tank.</summary>
        public static double DesiredPitch(Double3 specificForceTankFrame)
        {
            var f = specificForceTankFrame;
            return Math.Atan2(f.X, Math.Max(1e-6, f.Z));
        }

        /// <summary>Desired roll (rad) to bring the specific force onto +Z_tank.</summary>
        public static double DesiredRoll(Double3 specificForceTankFrame)
        {
            var f = specificForceTankFrame;
            return Math.Atan2(f.Y, Math.Max(1e-6, f.Z));
        }
    }
}
