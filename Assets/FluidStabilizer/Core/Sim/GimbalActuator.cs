using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>Ground-truth state of one gimbal axis.</summary>
    public struct GimbalAxisState
    {
        public double Angle;            // rad
        public double Rate;             // rad/s
        public double AngularAccel;     // rad/s²
        public double AppliedTorque;    // N·m at the joint output
        public double MotorCurrent;     // A (∝ applied torque)
        public bool AtAngleLimit;
        public bool TorqueSaturated;
    }

    /// <summary>Combined pitch+roll gimbal state.</summary>
    public struct GimbalState
    {
        public GimbalAxisState Pitch;
        public GimbalAxisState Roll;
    }

    /// <summary>
    /// Two-axis stabilizer actuator (design-review §9). Per axis: transport delay
    /// on the rate command, a first-order rate loop producing a torque demand,
    /// peak-torque saturation, Coulomb + viscous joint friction, rate and travel
    /// limits with a soft end-stop, and an external load-torque input (slosh
    /// reaction + gravity torque) so the same command yields a different motion
    /// under different load. Backlash is present but disabled by default
    /// (<see cref="GimbalConfig.BacklashRad"/> = 0) until the slosh model lands.
    /// </summary>
    public sealed class GimbalActuator
    {
        private sealed class AxisSim
        {
            public double Angle;
            public double Rate;
            public double Setpoint;      // integrated commanded-rate position target
            public double Integral;      // servo position-error integral (anti-windup clamped)
            public double LastAccel;
            public double LastAccelSmoothed;
            public double LastTorque;
            public double LastCurrent;
            public bool AtLimit;
            public bool Saturated;
            public DelayLine CommandDelay;
            public double ReflectedInertia;
            public Core.LowPass AccelFilter;
        }

        private readonly GimbalConfig _cfg;
        private readonly AxisSim _pitch = new AxisSim();
        private readonly AxisSim _roll = new AxisSim();
        private readonly double _torquePerAmp; // Kt·gear, for the current estimate
        private readonly double _stepDt;

        public GimbalActuator(GimbalConfig cfg, double dt)
        {
            _cfg = cfg;
            _pitch.CommandDelay = new DelayLine(cfg.CommandDelaySeconds, dt);
            _roll.CommandDelay = new DelayLine(cfg.CommandDelaySeconds, dt);
            _pitch.AccelFilter = new Core.LowPass(18.0);
            _roll.AccelFilter = new Core.LowPass(18.0);
            _stepDt = dt;
            // Pick Kt so peak torque corresponds to a ~20 A peak current.
            _torquePerAmp = cfg.MaxTorqueNm / 20.0;
            Reset();
        }

        public void Reset()
        {
            foreach (var a in new[] { _pitch, _roll })
            {
                a.Angle = 0; a.Rate = 0; a.Setpoint = 0; a.Integral = 0; a.LastAccel = 0; a.LastTorque = 0;
                a.LastAccelSmoothed = 0; a.LastCurrent = 0; a.AtLimit = false; a.Saturated = false;
                a.ReflectedInertia = 0.0;
                a.CommandDelay.Reset(0.0);
                a.AccelFilter.Reset(0.0);
            }
        }

        /// <summary>
        /// Set the liquid inertia reflected onto each axis (kg·m²). Added to the
        /// cradle inertia so the actuator "feels" the liquid mass without an
        /// unstable coupling loop (see <see cref="IReflectedInertiaSource"/>).
        /// </summary>
        public void SetReflectedInertia(double pitch, double roll)
        {
            _pitch.ReflectedInertia = Math.Max(0.0, pitch);
            _roll.ReflectedInertia = Math.Max(0.0, roll);
        }

        public GimbalState State => new GimbalState
        {
            Pitch = Snapshot(_pitch),
            Roll = Snapshot(_roll)
        };

        public double AngleOf(Axis axis) => (axis == Axis.Pitch ? _pitch : _roll).Angle;

        /// <summary>
        /// Advance both axes one step.
        /// <paramref name="externalLoadTorquePitch"/>/<c>Roll</c> is the torque the
        /// rest of the system applies to each joint output (N·m): slosh reaction
        /// about that axis plus tank gravity torque. Positive = aids positive rate.
        /// </summary>
        public void Step(in StabilizerCommand command, double externalLoadTorquePitch,
            double externalLoadTorqueRoll, double dt)
        {
            bool angleMode = command.Mode == StabilizerCommandMode.JointAngle;
            StepAxis(_pitch, command.PitchCommand, angleMode, externalLoadTorquePitch, dt);
            StepAxis(_roll, command.RollCommand, angleMode, externalLoadTorqueRoll, dt);
        }

        private void StepAxis(AxisSim a, double command, bool angleMode, double externalLoad, double dt)
        {
            double cmd = a.CommandDelay.Step(angleMode
                ? MathD.ClampAbs(command, _cfg.MaxAngleRad)
                : MathD.ClampAbs(command, _cfg.MaxRateRadPerSec));

            double inertia = _cfg.AxisInertia + a.ReflectedInertia;

            // Rate commands integrate into the position setpoint; angle commands
            // set it directly. Either way the axis then servos to the setpoint,
            // which holds angle under a steady external load (a pure rate loop
            // would drift).
            if (angleMode)
                a.Setpoint = cmd;
            else
                a.Setpoint = MathD.ClampAbs(a.Setpoint + cmd * dt, _cfg.MaxAngleRad);

            // For the servo's rate-feedforward term, the effective rate command.
            double rateFeedforward = angleMode ? 0.0 : cmd;

            double omega = 2.0 * Math.PI * _cfg.BandwidthHz;
            double kp = omega * omega;
            double kd = 2.0 * _cfg.DampingRatio * omega;
            double posError = a.Setpoint - a.Angle;

            // Integral term with anti-windup: freeze it while the torque is
            // saturated or the joint is on its travel limit.
            if (!a.Saturated && !a.AtLimit)
                a.Integral = MathD.ClampAbs(a.Integral + posError * dt, _cfg.MaxAngleRad);

            double accelDemand = kp * posError + kd * (rateFeedforward - a.Rate) + _cfg.IntegralGain * a.Integral;

            // Clamp the external load that the servo has to fight, so a bad
            // transient in an upstream model cannot inject a divergent torque.
            double load = MathD.ClampAbs(externalLoad, 4.0 * _cfg.MaxTorqueNm);

            double friction = _cfg.ViscousFrictionNmsPerRad * a.Rate
                              + _cfg.CoulombFrictionNm * Sign(a.Rate);

            double torqueDemand = inertia * accelDemand + friction - load;

            double torqueApplied = MathD.ClampAbs(torqueDemand, _cfg.MaxTorqueNm);
            a.Saturated = Math.Abs(torqueDemand) > _cfg.MaxTorqueNm + 1e-9;

            double accel = (torqueApplied - friction + load) / inertia;

            a.Rate = MathD.ClampAbs(a.Rate + accel * dt, _cfg.MaxRateRadPerSec);
            double newAngle = a.Angle + a.Rate * dt;

            a.AtLimit = false;
            if (newAngle > _cfg.MaxAngleRad)
            {
                newAngle = _cfg.MaxAngleRad;
                if (a.Rate > 0) a.Rate = 0;
                a.Setpoint = _cfg.MaxAngleRad;
                a.AtLimit = true;
            }
            else if (newAngle < -_cfg.MaxAngleRad)
            {
                newAngle = -_cfg.MaxAngleRad;
                if (a.Rate < 0) a.Rate = 0;
                a.Setpoint = -_cfg.MaxAngleRad;
                a.AtLimit = true;
            }

            a.LastAccel = accel;
            a.LastAccelSmoothed = a.AccelFilter.Step(accel, _stepDt);
            a.Angle = newAngle;
            a.LastTorque = torqueApplied;
            a.LastCurrent = torqueApplied / _torquePerAmp;
        }

        private GimbalAxisState Snapshot(AxisSim a) => new GimbalAxisState
        {
            Angle = a.Angle,
            Rate = a.Rate,
            AngularAccel = a.LastAccelSmoothed,
            AppliedTorque = a.LastTorque,
            MotorCurrent = a.LastCurrent,
            AtAngleLimit = a.AtLimit,
            TorqueSaturated = a.Saturated
        };

        // Smooth-ish sign with a small dead-band so a resting joint has no chatter.
        private static double Sign(double v)
        {
            const double eps = 1e-4;
            if (v > eps) return 1.0;
            if (v < -eps) return -1.0;
            return v / eps;
        }
    }
}
