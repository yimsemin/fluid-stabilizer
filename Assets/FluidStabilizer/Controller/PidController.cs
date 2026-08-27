using FluidStabilizer.Contracts;
using FluidStabilizer.Core;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// Sensor-only baseline: like <see cref="SpecificForceAlignmentController"/>
    /// but adds a clamped integral term on the residual tilt error, so a steady
    /// load (a long hill, a persistent bias) is trimmed out. Commands a target
    /// joint angle; the actuator servo tracks it.
    /// </summary>
    public sealed class PidController : IController
    {
        private readonly double _ki, _cutoffHz, _integralClamp;
        private readonly LowPass _fx, _fy, _fz;
        private double _iPitch, _iRoll;
        private long _seq;
        private double _lastStamp;

        public PidController(
            double ki = 0.4, double specificForceCutoffHz = 0.7, double integralClampRad = 0.15)
        {
            _ki = ki;
            _cutoffHz = specificForceCutoffHz;
            _integralClamp = integralClampRad;
            _fx = new LowPass(_cutoffHz);
            _fy = new LowPass(_cutoffHz);
            _fz = new LowPass(_cutoffHz);
        }

        public string Name => "SpecificForceAlignment+I";

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

            double tgtPitch = TiltGeometry.DesiredPitch(fLp);
            double tgtRoll = TiltGeometry.DesiredRoll(fLp);

            _iPitch = MathD.ClampAbs(_iPitch + (tgtPitch - s.Joints.PitchAngle) * dt, _integralClamp);
            _iRoll = MathD.ClampAbs(_iRoll + (tgtRoll - s.Joints.RollAngle) * dt, _integralClamp);

            return StabilizerCommand.Angle(_seq++, s.StampSeconds,
                tgtPitch + _ki * _iPitch, tgtRoll + _ki * _iRoll);
        }
    }
}
