using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim.SloshModels
{
    /// <summary>
    /// 2-D depth-averaged shallow-water (Saint-Venant) slosh model on a coarse
    /// grid over the tank floor, integrated in managed code at the physics rate
    /// with deterministic CFL-limited sub-stepping. First-order finite-volume
    /// Rusanov (local Lax-Friedrichs) flux, positivity-preserving, reflective
    /// walls, weir overflow at the rim. Drop-in behind <see cref="ISloshModel"/>
    /// for <see cref="PendulumSloshModel"/> (design-review §6, phase-2 model A).
    ///
    /// <para><b>Frame.</b> The model's internal grid has its origin at the centre
    /// of the tank floor, axes aligned with the Tank frame (x forward, y left,
    /// z up), with z = 0 at the floor and z = <c>TankHeightZ</c> at the rim. This
    /// matches the effective convention of <see cref="PendulumSloshModel"/>,
    /// which also returns its reaction torque about the floor centre.</para>
    ///
    /// <para><b>Coupling</b> (decision D3). The solver sees the true tank motion
    /// — gravity + linear acceleration + the full rotational terms, including the
    /// tank angular acceleration (Euler term) — as the depth-averaged momentum
    /// source. The reaction returned to the tank is quasi-steady: the liquid mass
    /// distribution times its non-inertial body force, <i>excluding</i> the tank
    /// angular-acceleration term. That term is delivered separately as a roughly
    /// constant reflected inertia (<see cref="IReflectedInertiaSource"/>),
    /// computed from the same analytic estimate <see cref="PendulumSloshModel"/>
    /// uses, so the actuator sees an identical rotational load under both models
    /// and only the wave dynamics differ. The wave's own momentum flux against
    /// the walls (a second-order reaction correction) is not fed back in v1.</para>
    /// </summary>
    public sealed class ShallowWaterSloshModel : ISloshModel, IReflectedInertiaSource
    {
        public string Name => "Shallow water (2-D Saint-Venant)";

        private readonly double _targetCell;

        /// <param name="targetCellSize">Nominal grid spacing (m). The tank is
        /// divided into the nearest whole number of cells, clamped to a sane
        /// range. 0.025 m over the nominal 1.2 × 0.7 m tank ⇒ 48 × 28.</param>
        public ShallowWaterSloshModel(double targetCellSize = 0.025)
        {
            _targetCell = targetCellSize > 0 ? targetCellSize : 0.025;
        }

        // ---- geometry (per episode) ----
        private int _nx, _ny;
        private double _lx, _ly, _rimZ, _dx, _dy, _cellA;
        private double _density, _h0, _restMass;
        private double[] _xc, _yc;

        // ---- state, row-major k = i*_ny + j ----
        private double[] _h, _hu, _hv;
        // x-face fluxes: (_nx+1) × _ny, index fi*_ny + j
        private double[] _fxh, _fxhu, _fxhv;
        // y-face fluxes: _nx × (_ny+1), index i*(_ny+1) + fj
        private double[] _fyh, _fyhu, _fyhv;

        // ---- parameters ----
        private double _cDragX, _cDragY, _cQuad, _freqScale2, _cd;
        private double _omegaX, _omegaY, _zeta;

        // ---- outputs cached between Step() and SampleGroundTruth() ----
        private Double3 _com;
        private double _ke, _thx, _thxDot, _thy, _thyDot;
        private double _thxPrev, _thyPrev;

        public double ReflectedInertiaPitch { get; private set; }
        public double ReflectedInertiaRoll { get; private set; }

        // ---- solver health counters (diagnostics; reset in Reset) ----
        /// <summary>Largest sub-step count used in any single physics step so far.</summary>
        public int MaxSubStepsUsed { get; private set; }
        /// <summary>Physics steps whose CFL estimate wanted more than <c>MaxSub</c> sub-steps.</summary>
        public long SubStepCapHits { get; private set; }
        /// <summary>Cells clamped to the |u| ≤ VMax ceiling (should stay 0 in a healthy run).</summary>
        public long VelocityClampHits { get; private set; }

        private const double HMin = 1e-6;   // never let a cell go negative
        private const double HDry = 1e-4;   // below this a cell carries no momentum
        private const double VMax = 8.0;    // hard velocity clamp (anti-NaN, see D3)
        private const double Cfl = 0.40;
        private const int MaxSub = 64;

        public void Reset(SloshParameters p, IRandomSource rng)
        {
            _lx = p.TankLengthX;
            _ly = p.TankWidthY;
            _rimZ = p.TankHeightZ;
            _density = p.LiquidDensity;
            _h0 = MathD.Clamp(p.FillRatio, 0.02, 0.98) * _rimZ;

            _nx = Math.Max(16, Math.Min(96, (int)Math.Round(_lx / _targetCell)));
            _ny = Math.Max(12, Math.Min(64, (int)Math.Round(_ly / _targetCell)));
            _dx = _lx / _nx;
            _dy = _ly / _ny;
            _cellA = _dx * _dy;

            _xc = new double[_nx];
            for (int i = 0; i < _nx; i++) _xc[i] = -_lx * 0.5 + (i + 0.5) * _dx;
            _yc = new double[_ny];
            for (int j = 0; j < _ny; j++) _yc[j] = -_ly * 0.5 + (j + 0.5) * _dy;

            int n = _nx * _ny;
            _h = new double[n];
            _hu = new double[n];
            _hv = new double[n];
            for (int k = 0; k < n; k++) _h[k] = _h0;

            _fxh = new double[(_nx + 1) * _ny];
            _fxhu = new double[(_nx + 1) * _ny];
            _fxhv = new double[(_nx + 1) * _ny];
            _fyh = new double[_nx * (_ny + 1)];
            _fyhu = new double[_nx * (_ny + 1)];
            _fyhv = new double[_nx * (_ny + 1)];

            _restMass = _density * _lx * _ly * _h0;

            double fscale = p.NaturalFrequencyScale > 0 ? p.NaturalFrequencyScale : 1.0;
            _omegaX = RectFirstModeOmega(_lx, _h0) * fscale;
            _omegaY = RectFirstModeOmega(_ly, _h0) * fscale;
            _freqScale2 = fscale * fscale;
            _zeta = Math.Max(0.0, p.DampingRatio);

            // Linear velocity drag a = -c·u; a mode with this drag decays as
            // exp(-(c/2)t), so c = 2·ζ·ωₙ reproduces the target damping ratio.
            _cDragX = 2.0 * _zeta * _omegaX;
            _cDragY = 2.0 * _zeta * _omegaY;
            _cQuad = Math.Max(0.0, p.QuadraticDamping);
            _cd = p.SpillDischargeCoefficient > 0 ? p.SpillDischargeCoefficient : 0.6;

            _thx = _thxDot = _thy = _thyDot = _thxPrev = _thyPrev = 0.0;
            _com = new Double3(0, 0, 0.5 * _h0);
            _ke = 0.0;
            MaxSubStepsUsed = 0;
            SubStepCapHits = 0;
            VelocityClampHits = 0;

            // Reflected inertia — the analytic equivalent-mechanical estimate,
            // replicated from PendulumSloshModel so the actuator loop sees the
            // same rotational load under both models.
            double mFluid = _restMass;
            double fx = MathD.Clamp(RectFirstModeMassFraction(_lx, _h0) * p.ParticipatingMassScale, 0.0, 0.45);
            double fy = MathD.Clamp(RectFirstModeMassFraction(_ly, _h0) * p.ParticipatingMassScale, 0.0, 0.45);
            double m1x = mFluid * fx, m1y = mFluid * fy;
            double m0 = Math.Max(0.0, mFluid - m1x - m1y);
            double lPendX = MathD.StandardGravity / (_omegaX * _omegaX);
            double lPendY = MathD.StandardGravity / (_omegaY * _omegaY);
            double pivotZ = _h0, z0 = 0.5 * _h0;
            double zBobX = pivotZ - lPendX, zBobY = pivotZ - lPendY;
            ReflectedInertiaPitch = m1x * zBobX * zBobX + m0 * z0 * z0;
            ReflectedInertiaRoll = m1y * zBobY * zBobY + m0 * z0 * z0;
        }

        public SloshOutput Step(in SloshInput input)
        {
            double dtp = input.Dt;
            Double3 g = input.GravityTankFrame;
            Double3 aO = input.LinearAccelerationTankFrame;
            Double3 om = input.AngularVelocityTankFrame;
            Double3 al = input.AngularAccelerationTankFrame;

            // Uniform gravity-wave parameter: the vertical effective gravity at
            // the tank centre. Keeping it spatially uniform keeps the scheme
            // well-balanced (lake-at-rest is preserved exactly). The horizontal
            // body force, which drives the sloshing, is applied per cell.
            double gWave = MathD.Clamp((-(g.Z - aO.Z)) * _freqScale2, 2.0, 40.0);

            // Deterministic sub-step count from a CFL estimate (× 1.5 margin).
            double smax = 0.0;
            for (int k = 0; k < _h.Length; k++)
            {
                double h = _h[k];
                if (h < HDry) continue;
                double u = _hu[k] / h, v = _hv[k] / h;
                double c = Math.Sqrt(gWave * h);
                double s = (Math.Abs(u) + c) / _dx + (Math.Abs(v) + c) / _dy;
                if (s > smax) smax = s;
            }
            int wantSub = smax > 0 ? (int)Math.Ceiling(1.5 * dtp * smax / Cfl) : 1;
            int nSub = Math.Max(1, Math.Min(MaxSub, wantSub));
            if (wantSub > MaxSub) SubStepCapHits++;
            if (nSub > MaxSubStepsUsed) MaxSubStepsUsed = nSub;
            double dt = dtp / nSub;

            double spill = 0.0;
            for (int s = 0; s < nSub; s++)
                spill += Substep(dt, gWave, g, aO, om, al);

            // ---- reaction on the tank + ground-truth read-outs (α excluded) ----
            Double3 force = Double3.Zero, torque = Double3.Zero, comAcc = Double3.Zero;
            double mass = 0.0, ke = 0.0, maxH = 0.0;
            for (int i = 0; i < _nx; i++)
            for (int j = 0; j < _ny; j++)
            {
                int k = i * _ny + j;
                double h = _h[k];
                if (h > maxH) maxH = h;
                if (h < HDry) continue;

                double u = _hu[k] / h, v = _hv[k] / h;
                double m = _density * h * _cellA;
                var r = new Double3(_xc[i], _yc[j], 0.5 * h);

                Double3 centrifugal = Double3.Cross(om, Double3.Cross(om, r));
                Double3 coriolis = 2.0 * Double3.Cross(om, new Double3(u, v, 0.0));
                // Body force per unit mass, tank angular acceleration omitted.
                Double3 fb = g - aO - centrifugal - coriolis;

                Double3 fCell = m * fb;         // parcel reaction on the tank
                force += fCell;
                torque += Double3.Cross(r, fCell);

                mass += m;
                comAcc += m * r;
                ke += 0.5 * m * (u * u + v * v);
            }
            _com = mass > 1e-9 ? comAcc / mass : new Double3(0, 0, 0.5 * _h0);
            _ke = ke;

            // Equivalent slosh angles from the mean free-surface slope.
            double sx = 0.0, sy = 0.0, sxx = 0.0, syy = 0.0;
            for (int i = 0; i < _nx; i++)
            {
                double hm = 0.0;
                for (int j = 0; j < _ny; j++) hm += _h[i * _ny + j];
                hm /= _ny;
                sx += _xc[i] * hm;
                sxx += _xc[i] * _xc[i];
            }
            for (int j = 0; j < _ny; j++)
            {
                double hm = 0.0;
                for (int i = 0; i < _nx; i++) hm += _h[i * _ny + j];
                hm /= _nx;
                sy += _yc[j] * hm;
                syy += _yc[j] * _yc[j];
            }
            double slopeX = sxx > 1e-12 ? sx / sxx : 0.0;
            double slopeY = syy > 1e-12 ? sy / syy : 0.0;
            _thx = Math.Atan(slopeX);
            _thy = Math.Atan(slopeY);
            _thxDot = (_thx - _thxPrev) / dtp;
            _thyDot = (_thy - _thyPrev) / dtp;
            _thxPrev = _thx;
            _thyPrev = _thy;

            return new SloshOutput
            {
                ReactionForceTankFrame = force,
                ReactionTorqueTankFrame = torque,
                OverflowVolumeThisStep = spill,
                MinFreeboard = _rimZ - maxH
            };
        }

        public SloshGroundTruth SampleGroundTruth() => new SloshGroundTruth
        {
            LiquidCenterOfMass = _com,
            SloshKineticEnergy = _ke,
            ModeState = new[] { _thx, _thxDot, _thy, _thyDot },
            PitchModeOmega = _omegaX,
            RollModeOmega = _omegaY,
            ModeDampingRatio = _zeta
        };

        // ---------------------------------------------------------------- solver

        private double Substep(double dt, double gWave, Double3 g, Double3 aO, Double3 om, Double3 al)
        {
            int nx = _nx, ny = _ny;
            double invdx = 1.0 / _dx, invdy = 1.0 / _dy;

            // ---- x-face fluxes ----
            for (int j = 0; j < ny; j++)
            {
                for (int fi = 1; fi < nx; fi++)
                {
                    int kL = (fi - 1) * ny + j, kR = fi * ny + j;
                    RusanovX(_h[kL], _hu[kL], _hv[kL], _h[kR], _hu[kR], _hv[kR], gWave,
                        out double a, out double b, out double c);
                    int f = fi * ny + j;
                    _fxh[f] = a; _fxhu[f] = b; _fxhv[f] = c;
                }
                // reflective walls: only the hydrostatic pressure passes
                double hb0 = Math.Max(_h[j], 0.0);
                _fxh[j] = 0.0; _fxhu[j] = 0.5 * gWave * hb0 * hb0; _fxhv[j] = 0.0;
                double hbN = Math.Max(_h[(nx - 1) * ny + j], 0.0);
                int fN = nx * ny + j;
                _fxh[fN] = 0.0; _fxhu[fN] = 0.5 * gWave * hbN * hbN; _fxhv[fN] = 0.0;
            }

            // ---- y-face fluxes ----
            for (int i = 0; i < nx; i++)
            {
                int rowBase = i * (ny + 1);
                for (int fj = 1; fj < ny; fj++)
                {
                    int kL = i * ny + (fj - 1), kR = i * ny + fj;
                    RusanovY(_h[kL], _hu[kL], _hv[kL], _h[kR], _hu[kR], _hv[kR], gWave,
                        out double a, out double b, out double c);
                    int f = rowBase + fj;
                    _fyh[f] = a; _fyhu[f] = b; _fyhv[f] = c;
                }
                double hb0 = Math.Max(_h[i * ny], 0.0);
                _fyh[rowBase] = 0.0; _fyhu[rowBase] = 0.0; _fyhv[rowBase] = 0.5 * gWave * hb0 * hb0;
                double hbN = Math.Max(_h[i * ny + (ny - 1)], 0.0);
                _fyh[rowBase + ny] = 0.0; _fyhu[rowBase + ny] = 0.0;
                _fyhv[rowBase + ny] = 0.5 * gWave * hbN * hbN;
            }

            // ---- cell update (fluxes decouple neighbours, so update in place) ----
            double spill = 0.0;
            for (int i = 0; i < nx; i++)
            {
                bool xEdge = i == 0 || i == nx - 1;
                int rowBase = i * (ny + 1);
                for (int j = 0; j < ny; j++)
                {
                    int k = i * ny + j;
                    int xf = i * ny + j, xf1 = (i + 1) * ny + j;
                    int yf = rowBase + j, yf1 = rowBase + j + 1;

                    double h = _h[k] - dt * ((_fxh[xf1] - _fxh[xf]) * invdx + (_fyh[yf1] - _fyh[yf]) * invdy);
                    double hu = _hu[k] - dt * ((_fxhu[xf1] - _fxhu[xf]) * invdx + (_fyhu[yf1] - _fyhu[yf]) * invdy);
                    double hv = _hv[k] - dt * ((_fxhv[xf1] - _fxhv[xf]) * invdx + (_fyhv[yf1] - _fyhv[yf]) * invdy);

                    if (h < HMin) h = HMin;

                    double u = h > HDry ? hu / h : 0.0;
                    double v = h > HDry ? hv / h : 0.0;

                    // non-inertial body force per unit mass at (xc, yc, h/2),
                    // full tank motion including the Euler (α × r) term.
                    double rx = _xc[i], ry = _yc[j], rz = 0.5 * h;
                    double wxr_x = om.Y * rz - om.Z * ry;
                    double wxr_y = om.Z * rx - om.X * rz;
                    double wxr_z = om.X * ry - om.Y * rx;
                    double cf_x = om.Y * wxr_z - om.Z * wxr_y;
                    double cf_y = om.Z * wxr_x - om.X * wxr_z;
                    double eu_x = al.Y * rz - al.Z * ry;
                    double eu_y = al.Z * rx - al.X * rz;
                    double co_x = 2.0 * (-om.Z * v);
                    double co_y = 2.0 * (om.Z * u);
                    double bx = g.X - aO.X - eu_x - cf_x - co_x;
                    double by = g.Y - aO.Y - eu_y - cf_y - co_y;

                    hu += dt * h * bx;
                    hv += dt * h * by;

                    // linear + quadratic (wave-breaking) drag
                    hu -= dt * (_cDragX * hu + _cQuad * hu * Math.Abs(u));
                    hv -= dt * (_cDragY * hv + _cQuad * hv * Math.Abs(v));

                    if (h < HDry)
                    {
                        hu = 0.0; hv = 0.0;
                    }
                    else
                    {
                        double uu = hu / h, vv = hv / h;
                        if (uu > VMax) { hu = VMax * h; VelocityClampHits++; }
                        else if (uu < -VMax) { hu = -VMax * h; VelocityClampHits++; }
                        if (vv > VMax) { hv = VMax * h; VelocityClampHits++; }
                        else if (vv < -VMax) { hv = -VMax * h; VelocityClampHits++; }
                    }

                    // weir overflow once the surface tops the rim at a wall cell
                    if (xEdge || j == 0 || j == ny - 1)
                    {
                        double head = h - _rimZ;
                        if (head > 0.0)
                        {
                            double weirPerLen = _cd * (2.0 / 3.0)
                                                * Math.Sqrt(2.0 * gWave) * head * Math.Sqrt(head);
                            double dh = 0.0;
                            if (xEdge) dh += weirPerLen * dt * invdx;
                            if (j == 0 || j == ny - 1) dh += weirPerLen * dt * invdy;
                            dh = Math.Min(dh, head * 0.9);
                            h -= dh;
                            spill += dh * _cellA;
                        }
                    }

                    _h[k] = h; _hu[k] = hu; _hv[k] = hv;
                }
            }
            return spill;
        }

        private static void RusanovX(
            double hL, double huL, double hvL, double hR, double huR, double hvR, double g,
            out double fh, out double fhu, out double fhv)
        {
            double hLp = Math.Max(hL, 1e-12), hRp = Math.Max(hR, 1e-12);
            double uL = huL / hLp, uR = huR / hRp;
            double vL = hvL / hLp, vR = hvR / hRp;
            double fhuL = huL * uL + 0.5 * g * hL * hL, fhuR = huR * uR + 0.5 * g * hR * hR;
            double a = Math.Max(Math.Abs(uL) + Math.Sqrt(g * hLp), Math.Abs(uR) + Math.Sqrt(g * hRp));
            fh = 0.5 * (huL + huR) - 0.5 * a * (hR - hL);
            fhu = 0.5 * (fhuL + fhuR) - 0.5 * a * (huR - huL);
            fhv = 0.5 * (huL * vL + huR * vR) - 0.5 * a * (hvR - hvL);
        }

        private static void RusanovY(
            double hL, double huL, double hvL, double hR, double huR, double hvR, double g,
            out double fh, out double fhu, out double fhv)
        {
            double hLp = Math.Max(hL, 1e-12), hRp = Math.Max(hR, 1e-12);
            double uL = huL / hLp, uR = huR / hRp;
            double vL = hvL / hLp, vR = hvR / hRp;
            double fhvL = hvL * vL + 0.5 * g * hL * hL, fhvR = hvR * vR + 0.5 * g * hR * hR;
            double a = Math.Max(Math.Abs(vL) + Math.Sqrt(g * hLp), Math.Abs(vR) + Math.Sqrt(g * hRp));
            fh = 0.5 * (hvL + hvR) - 0.5 * a * (hR - hL);
            fhu = 0.5 * (hvL * uL + hvR * uR) - 0.5 * a * (huR - huL);
            fhv = 0.5 * (fhvL + fhvR) - 0.5 * a * (hvR - hvL);
        }

        // Standard rectangular-tank first sloshing mode (Abramson / Dodge),
        // identical expressions to PendulumSloshModel.
        private static double RectFirstModeOmega(double tankLength, double depth)
        {
            double k = Math.PI / tankLength;
            return Math.Sqrt(MathD.StandardGravity * k * Math.Tanh(k * depth));
        }

        private static double RectFirstModeMassFraction(double tankLength, double depth)
        {
            double r = depth / tankLength;
            if (r < 1e-4) return 0.0;
            return 8.0 * Math.Tanh(Math.PI * r) / (Math.PI * Math.PI * Math.PI * r);
        }

        // ---- diagnostics for tests / visualisation (not the actor's) ----

        /// <summary>Grid dimensions (cells) chosen for the current episode.</summary>
        public (int nx, int ny) GridSize => (_nx, _ny);

        /// <summary>Surface height above the floor at cell (i, j), metres.</summary>
        public double SurfaceHeight(int i, int j) => _h[i * _ny + j];

        /// <summary>Cell-centre position in the floor-origin tank frame, metres.</summary>
        public Double3 CellCenter(int i, int j) => new Double3(_xc[i], _yc[j], 0.0);

        /// <summary>Total liquid volume currently in the tank (m³).</summary>
        public double LiquidVolume
        {
            get
            {
                double s = 0.0;
                for (int k = 0; k < _h.Length; k++) s += _h[k];
                return s * _cellA;
            }
        }
    }
}
