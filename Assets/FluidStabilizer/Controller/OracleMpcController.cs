using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Controller
{
    /// <summary>Tuning of <see cref="OracleMpcController"/>. Defaults are the best of the step-1 sweep (findings F14); the tilt weight and lock margin are what keep it from doing worse than rigid.</summary>
    public sealed class MpcOptions
    {
        /// <summary>Prediction horizon (s). Slosh periods are 1.0–1.4 s, so this spans a bit over one.</summary>
        public double HorizonSeconds = 1.5;
        /// <summary>Spacing of the optimised tilt knots (s); the command is linear between knots.</summary>
        public double KnotSeconds = 0.25;
        /// <summary>Internal model step (s). 200 Hz makes the 15 ms command delay exactly 3 steps.</summary>
        public double ModelStepSeconds = 1.0 / 200.0;
        /// <summary>Re-plan every N control ticks (100 Hz).</summary>
        public int ReplanEveryTicks = 3;

        public int Samples = 24;
        public int Iterations = 1;
        /// <summary>Std-dev of the knot noise (rad) on the first iteration; halves on the second.</summary>
        public double NoiseRad = 0.06;
        /// <summary>MPPI temperature (cost units); smaller = greedier.</summary>
        public double Temperature = 0.5;

        // ---- cost (litres-equivalent) ----
        public double SpillWeightPerLitre = 1.0;
        /// <summary>Freeboard below this (m) starts to cost; squared ramp to <see cref="FreeboardWeightPerSecond"/> at 0.</summary>
        public double FreeboardMarginM = 0.04;
        public double FreeboardWeightPerSecond = 0.5;
        /// <summary>Terminal slosh energy cost (litres per joule) — stops the plan ignoring what is left ringing.</summary>
        public double TerminalEnergyWeight = 0.02;
        /// <summary>Small penalty on tilt (litres per rad²·s) so the plan returns to level when nothing is gained.</summary>
        public double TiltWeight = 15.0;

        /// <summary>Lock when the rigid rollout costs no more than the best active plan plus this (litres).</summary>
        public double LockMarginLitres = 1.0;

        /// <summary>Time constant (s) over which the held disturbance decays toward level; 0 = hold forever.</summary>
        public double DisturbanceDecaySeconds = 0.0;

        public ulong Seed = 12345;
    }

    /// <summary>
    /// MPC step 1 (experiment-plan.md): an <b>oracle</b> model-predictive tilt
    /// controller. It is privileged — it reads the true slosh state — so it is an
    /// upper bound on what predictive control can do here, never a deployable
    /// controller.
    ///
    /// Each plan it (1) copies the plant — actuator servo + delay + torque limit
    /// and the pendulum slosh model, the very classes the simulation runs — from
    /// the current true state, (2) assumes the vehicle's present specific force
    /// and rotation stay as they are (it does <em>not</em> know the future road or
    /// driver), (3) searches tilt-angle sequences with MPPI (sampling, so the
    /// near-discontinuous spill cost needs no gradient), and (4) compares the best
    /// with the mechanically locked rigid rollout; the lock gate keeps the
    /// physical engage/dwell rules.
    /// </summary>
    public sealed class OracleMpcController : IPrivilegedController
    {
        // ---------- plant copy ----------
        private sealed class Plant
        {
            public readonly GimbalActuator Gimbal;
            public readonly PendulumSloshModel Slosh = new PendulumSloshModel();
            private readonly Double3 _pivot;
            private readonly double _dt;
            private VehicleState _vehicle;
            private double _loadPitch, _loadRoll;

            public Plant(SimulationConfig cfg, double dt)
            {
                _dt = dt;
                _pivot = cfg.Gimbal.PivotInVehicle;
                Gimbal = new GimbalActuator(cfg.Gimbal, dt);
                Slosh.Reset(cfg.Slosh.ToParameters(), new DeterministicRandom(0));
                Gimbal.SetReflectedInertia(Slosh.ReflectedInertiaPitch, Slosh.ReflectedInertiaRoll);
            }

            /// <summary>
            /// Disturbance: the vehicle's specific force at the pivot (vehicle frame) and
            /// body rotation, as a synthetic vehicle state with level orientation. Only
            /// specific force matters to the slosh, so a = F + g makes it exact.
            /// </summary>
            public void SetDisturbance(Double3 specificForceVehicle, Double3 omega, Double3 alpha)
            {
                Double3 gLevel = new Double3(0, 0, -MathD.StandardGravity);
                Double3 lever = Double3.Cross(alpha, _pivot) + Double3.Cross(omega, Double3.Cross(omega, _pivot));
                _vehicle = new VehicleState
                {
                    OrientationWorldFromBody = Double3x3.Identity,
                    LinearAccelerationBody = specificForceVehicle + gLevel - lever,
                    AngularVelocityBody = omega,
                    AngularAccelerationBody = alpha,
                };
            }

            public void ResetEpisode()
            {
                Gimbal.Reset(); // also clears the reflected inertia
                Gimbal.SetReflectedInertia(Slosh.ReflectedInertiaPitch, Slosh.ReflectedInertiaRoll);
            }

            public void CopyFrom(Plant o)
            {
                Gimbal.CopyStateFrom(o.Gimbal);
                Slosh.CopyStateFrom(o.Slosh);
                _vehicle = o._vehicle;
                _loadPitch = o._loadPitch;
                _loadRoll = o._loadRoll;
            }

            /// <summary>Advance one model step in the same order as <see cref="SimulationLoop"/>.</summary>
            public SloshOutput Step(in StabilizerCommand cmd)
            {
                Gimbal.Step(cmd, _loadPitch, _loadRoll, _dt);
                GimbalState g = Gimbal.State;
                TankKinematicState tank = TankKinematics.Compute(_vehicle, g, _pivot);
                SloshOutput o = Slosh.Step(new SloshInput
                {
                    GravityTankFrame = tank.GravityTankFrame,
                    LinearAccelerationTankFrame = tank.LinearAccelerationTankFrame,
                    AngularVelocityTankFrame = tank.AngularVelocityTankFrame,
                    AngularAccelerationTankFrame = tank.AngularAccelerationTankFrame,
                    Dt = _dt
                });
                SimulationLoop.JointLoad(o.ReactionTorqueTankFrame, g.Pitch.Angle, g.Roll.Angle,
                    out _loadPitch, out _loadRoll);
                return o;
            }
        }

        // ---------- state ----------
        private readonly SimulationConfig _cfg;
        private readonly MpcOptions _opt;
        private readonly double _maxAngle;
        private readonly int _knots;
        private readonly int _horizonSteps;
        private readonly int _stepsPerTick;
        private readonly LockGate _gate;
        private readonly Plant _shadow, _rollout;
        private Pcg32 _rng;

        private readonly double[] _nomP, _nomR, _bestP, _bestR, _candP, _candR, _sumP, _sumR;
        private readonly double[] _costs;
        private double[] _samP, _samR;

        private Double3 _fV, _omega, _alpha;
        private bool _started;
        private long _seq;
        private int _tick;
        private int _ticksSincePlan;
        private bool _lockRequest = true;
        private StabilizerCommand _lastCmd;

        /// <summary>Cost (litres-equivalent) of the last chosen active plan and of the rigid rollout; NaN when not evaluated.</summary>
        public double LastActiveCost { get; private set; } = double.NaN;
        public double LastLockCost { get; private set; } = double.NaN;
        public bool IsLocked => _gate.IsLocked;
        public int PlanCount { get; private set; }
        public long Ticks { get; private set; }
        public long LockedTicks { get; private set; }

        public OracleMpcController(SimulationConfig cfg, MpcOptions options = null)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _opt = options ?? new MpcOptions();
            _maxAngle = cfg.Gimbal.MaxAngleRad;
            _knots = Math.Max(2, (int)Math.Round(_opt.HorizonSeconds / _opt.KnotSeconds) + 1);
            _horizonSteps = (int)Math.Round(_opt.HorizonSeconds / _opt.ModelStepSeconds);
            _stepsPerTick = Math.Max(1, (int)Math.Round(cfg.ControlDecimation / cfg.PhysicsRateHz / _opt.ModelStepSeconds));
            _gate = new LockGate(cfg.Gimbal);
            _shadow = new Plant(cfg, _opt.ModelStepSeconds);
            _rollout = new Plant(cfg, _opt.ModelStepSeconds);
            _rng = new Pcg32(_opt.Seed);

            _nomP = new double[_knots]; _nomR = new double[_knots];
            _bestP = new double[_knots]; _bestR = new double[_knots];
            _candP = new double[_knots]; _candR = new double[_knots];
            _sumP = new double[_knots]; _sumR = new double[_knots];
            _costs = new double[_opt.Samples];
            _samP = new double[_opt.Samples * _knots];
            _samR = new double[_opt.Samples * _knots];
        }

        public string Name => "OracleMPC (privileged, upper bound)";

        public void Reset()
        {
            _started = false;
            _seq = 0;
            _tick = 0;
            _ticksSincePlan = 0;
            _lockRequest = true;
            _gate.Reset(startLocked: true);
            Array.Clear(_nomP, 0, _knots);
            Array.Clear(_nomR, 0, _knots);
            _lastCmd = StabilizerCommand.Hold(0, 0.0);
            LastActiveCost = LastLockCost = double.NaN;
            PlanCount = 0;
            Ticks = LockedTicks = 0;
            _shadow.ResetEpisode();
            _rng = new Pcg32(_opt.Seed); // same seed every episode: results are reproducible
        }

        public StabilizerCommand ComputeCommand(in SensorBundle s)
            => StabilizerCommand.Hold(_seq++, s.StampSeconds);

        public StabilizerCommand ComputeCommand(in SensorBundle s, in PrivilegedObservation p)
        {
            // 1. advance the dead-reckoned actuator copy over the interval that just elapsed
            if (_started)
                for (int i = 0; i < _stepsPerTick; i++) _shadow.Step(_lastCmd);
            _started = true;

            // 2. re-sync everything observable to the true state; hidden servo
            //    internals (integral, delay line, accel filter) stay dead-reckoned
            _shadow.Gimbal.OverwriteMeasured(p.PitchAngle, p.PitchRate, p.PitchThermalLoad,
                p.RollAngle, p.RollRate, p.RollThermalLoad);
            _shadow.Slosh.SetState(p.SloshPitchAngle, p.SloshPitchRate, p.SloshRollAngle, p.SloshRollRate,
                p.CumulativeOverflowVolume);
            _fV = Double3x3.Gimbal(p.PitchAngle, p.RollAngle) * p.SpecificForceTankFrame;
            _omega = p.VehicleAngularVelocityBody;
            _alpha = p.VehicleAngularAccelerationBody;
            _shadow.SetDisturbance(_fV, _omega, _alpha);

            // 3. plan
            if (_tick % _opt.ReplanEveryTicks == 0)
            {
                Plan(p.PitchAngle, p.RollAngle);
                _ticksSincePlan = 0;
            }
            _tick++;

            // 4. emit
            bool locked = _gate.ShouldLockExplicit(_lockRequest, p.PitchAngle, p.RollAngle);
            Ticks++;
            if (locked) LockedTicks++;
            StabilizerCommand cmd;
            if (locked)
            {
                cmd = StabilizerCommand.Hold(_seq++, s.StampSeconds);
            }
            else
            {
                double t = _ticksSincePlan * _cfg.ControlDecimation / _cfg.PhysicsRateHz;
                cmd = StabilizerCommand.Angle(_seq++, s.StampSeconds,
                    Interp(_nomP, t), Interp(_nomR, t));
            }
            _ticksSincePlan++;
            _lastCmd = cmd;
            return cmd;
        }

        // ---------- planning ----------
        private void Plan(double pitchAngle, double rollAngle)
        {
            PlanCount++;

            // shift the previous nominal forward by the time since it was made
            double elapsed = _opt.ReplanEveryTicks * _cfg.ControlDecimation / _cfg.PhysicsRateHz;
            for (int k = 0; k < _knots; k++)
            {
                double t = k * _opt.KnotSeconds + (PlanCount > 1 ? elapsed : 0.0);
                _candP[k] = Interp(_nomP, t);
                _candR[k] = Interp(_nomR, t);
            }
            Array.Copy(_candP, _nomP, _knots);
            Array.Copy(_candR, _nomR, _knots);

            double bestCost = double.PositiveInfinity;

            for (int it = 0; it < _opt.Iterations; it++)
            {
                double sigma = _opt.NoiseRad * Math.Pow(0.5, it);

                // sample sequences
                for (int i = 0; i < _opt.Samples; i++)
                {
                    int o = i * _knots;
                    for (int k = 0; k < _knots; k++)
                    {
                        double nP = _nomP[k], nR = _nomR[k];
                        if (i == 1) { nP = 0.0; nR = 0.0; }
                        else if (i == 2)
                        {
                            nP = Math.Atan2(_fV.X, Math.Max(1e-6, _fV.Z));
                            nR = Math.Atan2(_fV.Y, Math.Max(1e-6, _fV.Z));
                        }
                        else if (i > 2)
                        {
                            nP += sigma * Gauss();
                            nR += sigma * Gauss();
                        }
                        _samP[o + k] = MathD.ClampAbs(nP, _maxAngle);
                        _samR[o + k] = MathD.ClampAbs(nR, _maxAngle);
                    }
                }

                // evaluate
                double minCost = double.PositiveInfinity;
                int minIdx = 0;
                for (int i = 0; i < _opt.Samples; i++)
                {
                    _costs[i] = Rollout(_samP, _samR, i * _knots, locked: false);
                    if (_costs[i] < minCost) { minCost = _costs[i]; minIdx = i; }
                }

                // MPPI weighted average
                Array.Clear(_sumP, 0, _knots);
                Array.Clear(_sumR, 0, _knots);
                double wsum = 0.0;
                for (int i = 0; i < _opt.Samples; i++)
                {
                    double w = Math.Exp(-(_costs[i] - minCost) / _opt.Temperature);
                    wsum += w;
                    for (int k = 0; k < _knots; k++)
                    {
                        _sumP[k] += w * _samP[i * _knots + k];
                        _sumR[k] += w * _samR[i * _knots + k];
                    }
                }
                for (int k = 0; k < _knots; k++) { _sumP[k] /= wsum; _sumR[k] /= wsum; }

                // keep the better of the averaged plan and the best single sample
                double avgCost = Rollout(_sumP, _sumR, 0, locked: false);
                if (avgCost <= minCost)
                {
                    Array.Copy(_sumP, _nomP, _knots);
                    Array.Copy(_sumR, _nomR, _knots);
                    bestCost = avgCost;
                }
                else
                {
                    Array.Copy(_samP, minIdx * _knots, _nomP, 0, _knots);
                    Array.Copy(_samR, minIdx * _knots, _nomR, 0, _knots);
                    bestCost = minCost;
                }
            }

            LastActiveCost = bestCost;

            // the rigid candidate — only meaningful when the joint is close enough to level to engage
            bool canEngage = Math.Max(Math.Abs(pitchAngle), Math.Abs(rollAngle)) < _cfg.Gimbal.LockAngleThresholdRad;
            if (canEngage || _gate.IsLocked)
            {
                LastLockCost = Rollout(null, null, 0, locked: true);
                _lockRequest = LastLockCost <= bestCost + _opt.LockMarginLitres;
            }
            else
            {
                LastLockCost = double.NaN;
                _lockRequest = false;
            }
        }

        /// <summary>Roll the plant copy forward under one tilt sequence (or locked) and return its cost.</summary>
        private double Rollout(double[] seqP, double[] seqR, int offset, bool locked)
        {
            _rollout.CopyFrom(_shadow);
            double dt = _opt.ModelStepSeconds;
            double cost = 0.0;

            double decay = _opt.DisturbanceDecaySeconds > 0.0 ? Math.Exp(-dt / _opt.DisturbanceDecaySeconds) : 1.0;
            Double3 fRest = new Double3(0, 0, MathD.StandardGravity);
            Double3 f = _fV;

            for (int i = 0; i < _horizonSteps; i++)
            {
                StabilizerCommand cmd;
                double tp = 0.0, tr = 0.0;
                if (locked) cmd = StabilizerCommand.Hold(0, 0.0);
                else
                {
                    double t = i * dt / _opt.KnotSeconds;
                    tp = Interp(seqP, offset, t);
                    tr = Interp(seqR, offset, t);
                    cmd = StabilizerCommand.Angle(0, 0.0, tp, tr);
                }

                if (decay < 1.0)
                {
                    f = fRest + (f - fRest) * decay;
                    _rollout.SetDisturbance(f, _omega * decay, _alpha * decay);
                }

                SloshOutput o = _rollout.Step(cmd);

                cost += _opt.SpillWeightPerLitre * o.OverflowVolumeThisStep * 1000.0;
                if (o.MinFreeboard < _opt.FreeboardMarginM)
                {
                    double r = (_opt.FreeboardMarginM - o.MinFreeboard) / _opt.FreeboardMarginM;
                    cost += _opt.FreeboardWeightPerSecond * r * r * dt;
                }
                cost += _opt.TiltWeight * (tp * tp + tr * tr) * dt;
            }

            cost += _opt.TerminalEnergyWeight * _rollout.Slosh.ModeEnergy;
            return cost;
        }

        // ---------- helpers ----------
        private double Interp(double[] knots, double timeSeconds) => Interp(knots, 0, timeSeconds / _opt.KnotSeconds);

        /// <summary>Linear interpolation over knot index units <paramref name="u"/>, holding the last value.</summary>
        private double Interp(double[] knots, int offset, double u)
        {
            if (u <= 0.0) return knots[offset];
            int n = _knots;
            if (u >= n - 1) return knots[offset + n - 1];
            int i = (int)u;
            double a = u - i;
            return knots[offset + i] * (1.0 - a) + knots[offset + i + 1] * a;
        }

        private double Gauss()
        {
            double u1 = Math.Max(1e-12, _rng.NextDouble());
            double u2 = _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
