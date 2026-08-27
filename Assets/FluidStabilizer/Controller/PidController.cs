using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// Tuned PID baseline (design-review §2): PID on the tank-tilt error (angle
    /// between the tank floor and the low-passed specific force), with the
    /// derivative taken from the measured joint rate (no noisy differentiation)
    /// and clamped integral action for steady loads such as a long hill.
    /// </summary>
    public sealed class PidController : IController
    {
        private readonly double _kp, _ki, _kd, _cutoffHz, _integralClamp;
        private readonly LowPass _fx, _fy, _fz;
        private double _iPitch, _iRoll;
        private long _seq;
        private double _lastStamp;

        public PidController(
            double kp = 5.0, double ki = 2.5, double kd = 0.8,
            double specificForceCutoffHz = 1.5, double integralClampRad = 0.3)
        {
            _kp = kp; _ki = ki; _kd = kd;
            _cutoffHz = specificForceCutoffHz;
            _integralClamp = integralClampRad;
            _fx = new LowPass(_cutoffHz);
            _fy = new LowPass(_cutoffHz);
            _fz = new LowPass(_cutoffHz);
        }

        public string Name => "PID";

        public void Reset()
        {
            _fx.Reset(); _fy.Reset(); _fz.Reset();
            _iPitch = _iRoll = 0.0;
            _seq = 0;
            _lastStamp = 0.0;
        }

        public StabilizerCommand ComputeCommand(in SensorBundle s)
        {
            double dt = s.StampSeconds - _lastStamp;
            if (dt <= 0.0) dt = 0.01;
            _lastStamp = s.StampSeconds;

            var f = s.TankImu.SpecificForce;
            var fLp = new Double3(_fx.Step(f.X, dt), _fy.Step(f.Y, dt), _fz.Step(f.Z, dt));

            double ePitch = TiltGeometry.DesiredPitch(fLp) - s.Joints.PitchAngle;
            double eRoll = TiltGeometry.DesiredRoll(fLp) - s.Joints.RollAngle;

            _iPitch = MathD.ClampAbs(_iPitch + ePitch * dt, _integralClamp);
            _iRoll = MathD.ClampAbs(_iRoll + eRoll * dt, _integralClamp);

            double pitchCmd = _kp * ePitch + _ki * _iPitch - _kd * s.Joints.PitchRate;
            double rollCmd = _kp * eRoll + _ki * _iRoll - _kd * s.Joints.RollRate;

            return new StabilizerCommand
            {
                Sequence = _seq++,
                FromStampSeconds = s.StampSeconds,
                PitchRateCommand = pitchCmd,
                RollRateCommand = rollCmd
            };
        }
    }
}
