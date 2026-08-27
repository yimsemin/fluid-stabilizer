using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// Shared geometry for the baseline controllers: the pitch/roll joint angles
    /// that keep the liquid surface level <em>relative to the tank</em> under a
    /// given specific force felt in the tank frame — i.e. the tilt that nulls the
    /// first-mode slosh pendulum.
    ///
    /// From the linearised pendulum equation θ̈ₛ = −ω²(θₛ + θ_g) − aₓ/L − 2ζωθ̇ₛ,
    /// the pendulum settles at θₛ = −θ_g − aₓ/g, so θ_g = −aₓ/g holds it at zero.
    /// The felt specific force is f = a − g_grav, so fₓ = aₓ and the target is
    /// θ_g = −fₓ/g ≈ −atan2(fₓ, f_z). (An earlier version had this sign wrong,
    /// which made the baselines tilt <em>into</em> the wave — see docs/findings.md
    /// F1 history.)
    /// </summary>
    public static class TiltGeometry
    {
        /// <summary>Desired pitch (rad) that keeps the surface level in the tank frame.</summary>
        public static double DesiredPitch(Double3 specificForceTankFrame)
        {
            var f = specificForceTankFrame;
            return Sign * Math.Atan2(f.X, Math.Max(1e-6, f.Z));
        }

        /// <summary>Desired roll (rad) that keeps the surface level in the tank frame.</summary>
        public static double DesiredRoll(Double3 specificForceTankFrame)
        {
            var f = specificForceTankFrame;
            return Sign * Math.Atan2(f.Y, Math.Max(1e-6, f.Z));
        }

        /// <summary>
        /// Sign of the felt-tilt → joint-angle map. Pinned by
        /// <c>TiltGeometrySignTests</c> against the actual plant rather than an
        /// analytic derivation (the sign is easy to get wrong; see findings F1).
        /// </summary>
        public const double Sign = 1.0;
    }
}
