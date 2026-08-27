using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim.SloshModels
{
    /// <summary>
    /// Equivalent-mechanical slosh model (design-review §6, option B): the first
    /// sloshing mode along each horizontal tank axis is represented by a planar
    /// pendulum, plus one rigidly-attached fixed mass for the non-sloshing
    /// remainder. Parameters follow the standard rectangular-tank results
    /// (Abramson, NASA SP-106 / Dodge):
    ///
    ///   ωₙ² = (π g / Lₜₐₙₖ)·tanh(π h / Lₜₐₙₖ),   L_pend = g / ωₙ²
    ///   m₁/m_fluid = 8·tanh(π h / Lₜₐₙₖ) / (π³ · h / Lₜₐₙₖ)
    ///
    /// The pitch pendulum swings in the tank X–Z plane, the roll pendulum in the
    /// Y–Z plane; they are integrated in the (rotating, accelerating) tank frame
    /// with gravity + translational + Euler + centrifugal terms (Coriolis is
    /// radial for planar motion and dropped). The full liquid reaction — fixed
    /// mass and both pendulums, including weight — is returned to the tank; the
    /// actuator inertia therefore excludes the liquid.
    ///
    /// Free-surface elevation at each wall is estimated from the pendulum angle
    /// via <see cref="SloshParameters.SurfaceSlopePerPendulumAngle"/> (needs
    /// calibration against a shallow-water run); overflow is a continuous
    /// weir-style estimate that also drains the fill volume.
    /// </summary>
    public sealed class PendulumSloshModel : ISloshModel, IReflectedInertiaSource
    {
        public string Name => "Pendulum ROM (1 mode / axis)";

        /// <summary>
        /// Liquid rotational inertia reflected onto each gimbal axis, evaluated at
        /// rest. The simulation adds this to the actuator inertia; the reaction
        /// torque this model returns therefore omits the angular-acceleration
        /// term (which would otherwise form an unstable explicit algebraic loop).
        /// </summary>
        public double ReflectedInertiaPitch { get; private set; }
        public double ReflectedInertiaRoll { get; private set; }

        // Geometry / masses (per episode).
        private double _tankLx, _tankLy, _tankH, _rimZ;
        private double _wallWidthX, _wallWidthY;
        private double _restSurfaceZ, _restSurfaceZ0;
        private double _liquidVolume, _fullLiquidVolume;
        private double _density;

        private double _m0, _m1x, _m1y;
        private double _Lx, _Ly;      // pendulum lengths
        private double _wx, _wy;      // natural frequencies (rad/s)
        private double _zeta;
        private double _surfaceSlopeGain;
        private double _dischargeCoef;
        private Double3 _pivot;        // pendulum pivot in tank frame
        private Double3 _fixedMassPos; // fixed mass position in tank frame

        // Dynamic state.
        private double _thx, _thxDot;  // pitch pendulum
        private double _thy, _thyDot;  // roll pendulum
        private double _lastSpillVolume;

        public void Reset(SloshParameters p, IRandomSource rng)
        {
            _tankLx = p.TankLengthX;
            _tankLy = p.TankWidthY;
            _tankH = p.TankHeightZ;
            _rimZ = p.TankHeightZ;
            _density = p.LiquidDensity;

            double h = MathD.Clamp(p.FillRatio, 0.01, 0.99) * _tankH;
            _restSurfaceZ = h;
            _restSurfaceZ0 = h;
            _fullLiquidVolume = _tankLx * _tankLy * h;
            _liquidVolume = _fullLiquidVolume;
            double mFluid = _density * _fullLiquidVolume;

            _wx = NaturalFreq(_tankLx, h) * p.NaturalFrequencyScale;
            _wy = NaturalFreq(_tankLy, h) * p.NaturalFrequencyScale;
            _Lx = MathD.StandardGravity / (_wx * _wx);
            _Ly = MathD.StandardGravity / (_wy * _wy);

            double fx = MathD.Clamp(FirstModeMassFraction(_tankLx, h) * p.ParticipatingMassScale, 0.0, 0.45);
            double fy = MathD.Clamp(FirstModeMassFraction(_tankLy, h) * p.ParticipatingMassScale, 0.0, 0.45);
            _m1x = mFluid * fx;
            _m1y = mFluid * fy;
            _m0 = Math.Max(0.0, mFluid - _m1x - _m1y);

            _zeta = Math.Max(0.0, p.DampingRatio);
            _surfaceSlopeGain = p.SurfaceSlopePerPendulumAngle;
            _dischargeCoef = p.SpillDischargeCoefficient;

            // Pivot near the still surface (standard first-mode placement); fixed
            // mass at the liquid centroid.
            _pivot = new Double3(0, 0, h);
            _fixedMassPos = new Double3(0, 0, 0.5 * h);

            _wallWidthX = _tankLy; // the ±X walls span the Y width
            _wallWidthY = _tankLx;

            _thx = _thxDot = _thy = _thyDot = 0.0;
            _lastSpillVolume = 0.0;

            // Reflected inertia about the pitch axis (tank Y) and roll axis (tank X),
            // taken at the rest configuration.
            double zBobX = _pivot.Z - _Lx, zBobY = _pivot.Z - _Ly;
            double z0 = _fixedMassPos.Z;
            ReflectedInertiaPitch = _m1x * zBobX * zBobX + _m0 * z0 * z0;
            ReflectedInertiaRoll = _m1y * zBobY * zBobY + _m0 * z0 * z0;
        }

        public SloshOutput Step(in SloshInput input)
        {
            double dt = input.Dt;
            Double3 gT = input.GravityTankFrame;
            Double3 aT = input.LinearAccelerationTankFrame;
            Double3 omega = input.AngularVelocityTankFrame;
            Double3 alpha = input.AngularAccelerationTankFrame;

            // --- integrate the two pendulums (semi-implicit Euler) ---
            // Integration sees the real tank angular acceleration.
            IntegratePitch(gT, aT, omega, alpha, dt);
            IntegrateRoll(gT, aT, omega, alpha, dt);

            // --- reaction on the tank ---
            // The reaction is computed with alpha = 0: the angular-acceleration
            // part of the liquid reaction is delivered to the actuator as a
            // constant reflected inertia (ReflectedInertia*), not here, so the
            // slosh↔actuator coupling has no unstable explicit algebraic loop.
            Double3 z = Double3.Zero;
            (Double3 fx, _, _) = BobReaction(
                _m1x, _Lx, _thx, _thxDot, PitchAccel(gT, aT, omega, z), NPitch(_thx), TPitch(_thx),
                gT, aT, omega, z);
            (Double3 fy, _, _) = BobReaction(
                _m1y, _Ly, _thy, _thyDot, RollAccel(gT, aT, omega, z), NRoll(_thy), TRoll(_thy),
                gT, aT, omega, z);

            Double3 aFixed = aT
                             + Double3.Cross(omega, Double3.Cross(omega, _fixedMassPos));
            Double3 fFixed = -_m0 * (aFixed - gT);

            Double3 reactionForce = fx + fy + fFixed;
            Double3 reactionTorque =
                Double3.Cross(_pivot, fx) +
                Double3.Cross(_pivot, fy) +
                Double3.Cross(_fixedMassPos, fFixed);

            // --- free-surface elevation at the four walls & overflow ---
            double etaXplus = _surfaceSlopeGain * (_tankLx * 0.5) * Math.Sin(_thx);
            double etaYplus = _surfaceSlopeGain * (_tankLy * 0.5) * Math.Sin(_thy);

            double fbXp = _rimZ - (_restSurfaceZ + etaXplus);
            double fbXm = _rimZ - (_restSurfaceZ - etaXplus);
            double fbYp = _rimZ - (_restSurfaceZ + etaYplus);
            double fbYm = _rimZ - (_restSurfaceZ - etaYplus);
            double minFreeboard = Math.Min(Math.Min(fbXp, fbXm), Math.Min(fbYp, fbYm));

            double spill = 0.0;
            spill += WeirOutflow(-fbXp, _wallWidthX, (_tankLx * 0.5) * _thxDot, dt);
            spill += WeirOutflow(-fbXm, _wallWidthX, (_tankLx * 0.5) * _thxDot, dt);
            spill += WeirOutflow(-fbYp, _wallWidthY, (_tankLy * 0.5) * _thyDot, dt);
            spill += WeirOutflow(-fbYm, _wallWidthY, (_tankLy * 0.5) * _thyDot, dt);

            spill = Math.Min(spill, _liquidVolume);
            _liquidVolume -= spill;
            _lastSpillVolume = spill;

            // Constant cross-section, so surface height ∝ remaining volume.
            if (_fullLiquidVolume > 1e-12)
                _restSurfaceZ = _restSurfaceZ0 * (_liquidVolume / _fullLiquidVolume);

            return new SloshOutput
            {
                ReactionForceTankFrame = reactionForce,
                ReactionTorqueTankFrame = reactionTorque,
                OverflowVolumeThisStep = spill,
                MinFreeboard = minFreeboard
            };
        }

        public SloshGroundTruth SampleGroundTruth()
        {
            // Liquid CoM ≈ fixed-mass centroid shifted by the pendulum excursions.
            double totalM = _m0 + _m1x + _m1y;
            Double3 com = _fixedMassPos * _m0
                          + (_pivot + NPitch(_thx) * _Lx) * _m1x
                          + (_pivot + NRoll(_thy) * _Ly) * _m1y;
            if (totalM > 1e-9) com /= totalM;

            double ke = 0.5 * _m1x * Sq(_Lx * _thxDot) + 0.5 * _m1y * Sq(_Ly * _thyDot);

            return new SloshGroundTruth
            {
                LiquidCenterOfMass = com,
                SloshKineticEnergy = ke,
                ModeState = new[] { _thx, _thxDot, _thy, _thyDot }
            };
        }

        // ---- pitch pendulum (tank X–Z plane), θ from -Z toward +X ----
        private static Double3 NPitch(double th) => new Double3(Math.Sin(th), 0, -Math.Cos(th));
        private static Double3 TPitch(double th) => new Double3(Math.Cos(th), 0, Math.Sin(th));

        private double PitchAccel(Double3 gT, Double3 aT, Double3 omega, Double3 alpha)
        {
            Double3 rBob = _pivot + NPitch(_thx) * _Lx;
            Double3 s = gT - aT - Double3.Cross(alpha, rBob)
                        - Double3.Cross(omega, Double3.Cross(omega, rBob));
            double tangential = Double3.Dot(s, TPitch(_thx));
            return tangential / _Lx - 2.0 * _zeta * _wx * _thxDot;
        }

        // The ROM is only valid for moderate excursions; clamp to keep a bad
        // transient from cascading to NaN. Hitting this clamp in normal use means
        // the scenario is outside the model's validity (design-review §6).
        private const double MaxPendulumAngle = 1.3;      // rad (~75°)
        private const double MaxPendulumRate = 25.0;      // rad/s

        private void IntegratePitch(Double3 gT, Double3 aT, Double3 omega, Double3 alpha, double dt)
        {
            double acc = PitchAccel(gT, aT, omega, alpha);
            _thxDot = MathD.ClampAbs(_thxDot + acc * dt, MaxPendulumRate);
            _thx = MathD.ClampAbs(_thx + _thxDot * dt, MaxPendulumAngle);
        }

        // ---- roll pendulum (tank Y–Z plane), θ from -Z toward +Y ----
        private static Double3 NRoll(double th) => new Double3(0, Math.Sin(th), -Math.Cos(th));
        private static Double3 TRoll(double th) => new Double3(0, Math.Cos(th), Math.Sin(th));

        private double RollAccel(Double3 gT, Double3 aT, Double3 omega, Double3 alpha)
        {
            Double3 rBob = _pivot + NRoll(_thy) * _Ly;
            Double3 s = gT - aT - Double3.Cross(alpha, rBob)
                        - Double3.Cross(omega, Double3.Cross(omega, rBob));
            double tangential = Double3.Dot(s, TRoll(_thy));
            return tangential / _Ly - 2.0 * _zeta * _wy * _thyDot;
        }

        private void IntegrateRoll(Double3 gT, Double3 aT, Double3 omega, Double3 alpha, double dt)
        {
            double acc = RollAccel(gT, aT, omega, alpha);
            _thyDot = MathD.ClampAbs(_thyDot + acc * dt, MaxPendulumRate);
            _thy = MathD.ClampAbs(_thy + _thyDot * dt, MaxPendulumAngle);
        }

        private (Double3 force, Double3 rBob, Double3 aRel) BobReaction(
            double m1, double L, double th, double thDot, double thDdot,
            Double3 nHat, Double3 tHat, Double3 gT, Double3 aT, Double3 omega, Double3 alpha)
        {
            if (m1 <= 0.0) return (Double3.Zero, _pivot, Double3.Zero);

            Double3 rBob = _pivot + nHat * L;
            Double3 vRel = tHat * (L * thDot);
            Double3 aRel = tHat * (L * thDdot) - nHat * (L * thDot * thDot);

            Double3 aBob = aT
                           + Double3.Cross(alpha, rBob)
                           + Double3.Cross(omega, Double3.Cross(omega, rBob))
                           + 2.0 * Double3.Cross(omega, vRel)
                           + aRel;

            Double3 force = -m1 * (aBob - gT); // reaction on the tank, applied at the pivot
            return (force, rBob, aRel);
        }

        private double WeirOutflow(double headAboveRim, double wallWidth, double surfaceVel, double dt)
        {
            if (headAboveRim <= 0.0) return 0.0;
            // Volume crossing the rim this step ≈ Cd · width · head · |outward surface velocity| · dt,
            // with a floor so a static over-fill still drains slowly.
            double v = Math.Max(Math.Abs(surfaceVel), 0.2);
            return _dischargeCoef * wallWidth * headAboveRim * v * dt;
        }

        private static double NaturalFreq(double tankLength, double depth)
        {
            double k = Math.PI / tankLength;
            double w2 = MathD.StandardGravity * k * Math.Tanh(k * depth);
            return Math.Sqrt(w2);
        }

        private static double FirstModeMassFraction(double tankLength, double depth)
        {
            double r = depth / tankLength;
            if (r < 1e-4) return 0.0;
            return 8.0 * Math.Tanh(Math.PI * r) / (Math.PI * Math.PI * Math.PI * r);
        }

        private static double Sq(double x) => x * x;
    }
}
