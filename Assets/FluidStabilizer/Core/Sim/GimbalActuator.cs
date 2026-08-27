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
            public double LastAccel;
            public double LastTorque;
            public double LastCurrent;
            public bool AtLimit;
            public bool Saturated;
            public DelayLine CommandDelay;
        }

        private readonly GimbalConfig _cfg;
        private readonly AxisSim _pitch = new AxisSim();
        private readonly AxisSim _roll = new AxisSim();
        private readonly double _torquePerAmp; // Kt·gear, for the current estimate

        public GimbalActuator(GimbalConfig cfg, double dt)
        {
            _cfg = cfg;
            _pitch.CommandDelay = new DelayLine(cfg.CommandDelaySeconds, dt);
            _roll.CommandDelay = new DelayLine(cfg.CommandDelaySeconds, dt);
            // Pick Kt so peak torque corresponds to a ~20 A peak current.
            _torquePerAmp = cfg.MaxTorqueNm / 20.0;
            Reset();
        }

        public void Reset()
        {
            foreach (var a in new[] { _pitch, _roll })
            {
                a.Angle = 0; a.Rate = 0; a.LastAccel = 0; a.LastTorque = 0;
                a.LastCurrent = 0; a.AtLimit = false; a.Saturated = false;
                a.CommandDelay.Reset(0.0);
            }
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
            StepAxis(_pitch, command.PitchRateCommand, externalLoadTorquePitch, dt);
            StepAxis(_roll, command.RollRateCommand, externalLoadTorqueRoll, dt);
        }

        private void StepAxis(AxisSim a, double rateCommand, double externalLoad, double dt)
        {
            double cmd = a.CommandDelay.Step(MathD.ClampAbs(rateCommand, _cfg.MaxRateRadPerSec));

            // First-order rate loop: time constant 1/(2π·bandwidth).
            double loopGain = 2.0 * Math.PI * _cfg.BandwidthHz;
            double accelDemand = (cmd - a.Rate) * loopGain;

            double friction = _cfg.ViscousFrictionNmsPerRad * a.Rate
                              + _cfg.CoulombFrictionNm * Sign(a.Rate);

            double torqueDemand = _cfg.AxisInertia * accelDemand + friction - externalLoad;

            double torqueApplied = MathD.ClampAbs(torqueDemand, _cfg.MaxTorqueNm);
            a.Saturated = Math.Abs(torqueDemand) > _cfg.MaxTorqueNm + 1e-9;

            double accel = (torqueApplied - friction + externalLoad) / _cfg.AxisInertia;

            a.Rate = MathD.ClampAbs(a.Rate + accel * dt, _cfg.MaxRateRadPerSec);
            double newAngle = a.Angle + a.Rate * dt;

            a.AtLimit = false;
            if (newAngle > _cfg.MaxAngleRad)
            {
                newAngle = _cfg.MaxAngleRad;
                if (a.Rate > 0) a.Rate = 0;
                a.AtLimit = true;
            }
            else if (newAngle < -_cfg.MaxAngleRad)
            {
                newAngle = -_cfg.MaxAngleRad;
                if (a.Rate < 0) a.Rate = 0;
                a.AtLimit = true;
            }

            a.LastAccel = accel;
            a.Angle = newAngle;
            a.LastTorque = torqueApplied;
            a.LastCurrent = torqueApplied / _torquePerAmp;
        }

        private GimbalAxisState Snapshot(AxisSim a) => new GimbalAxisState
        {
            Angle = a.Angle,
            Rate = a.Rate,
            AngularAccel = a.LastAccel,
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
