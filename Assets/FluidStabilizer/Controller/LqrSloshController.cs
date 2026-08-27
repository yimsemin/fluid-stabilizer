using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Control;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// Performance-ceiling baseline (design-review §2, §12): a per-axis discrete
    /// LQR on the coupled slosh + gimbal model, using the true slosh state and
    /// the true modal frequency/damping (privileged). A slew-limited feed-forward
    /// tilt (from the slosh-free specific force) rejects the manoeuvre; the LQR
    /// regulates the residual slosh and the gimbal excursion.
    ///
    /// This is the honest ceiling the sensor-only baselines and any learned
    /// policy are scored against. It cheats and must never be deployed.
    /// </summary>
    public sealed class LqrSloshController : IPrivilegedController
    {
        private readonly double _controlPeriod;
        private readonly double _qSloshAngle, _qSloshRate, _qGimbal, _rInput;
        private readonly LowPass _ffPitch, _ffRoll;
        private AxisLqr _pitchLqr, _rollLqr;
        private bool _designed;
        private double _prevFfPitch, _prevFfRoll;
        private long _seq;
        private double _lastStamp;

        public LqrSloshController(
            double controlPeriodSeconds = 0.01,
            double qSloshAngle = 800.0, double qSloshRate = 40.0,
            double qGimbal = 6.0, double rInput = 1.0)
        {
            _controlPeriod = controlPeriodSeconds;
            _qSloshAngle = qSloshAngle;
            _qSloshRate = qSloshRate;
            _qGimbal = qGimbal;
            _rInput = rInput;
            _ffPitch = new LowPass(1.6);
            _ffRoll = new LowPass(1.6);
        }

        public string Name => "LQR (privileged ceiling)";

        public void Reset()
        {
            _ffPitch.Reset();
            _ffRoll.Reset();
            _designed = false;
            _prevFfPitch = _prevFfRoll = 0.0;
            _seq = 0;
            _lastStamp = 0.0;
        }

        public StabilizerCommand ComputeCommand(in SensorBundle s)
            => StabilizerCommand.Hold(_seq++, s.StampSeconds);

        public StabilizerCommand ComputeCommand(in SensorBundle s, in PrivilegedObservation p)
        {
            double dt = p.StampSeconds - _lastStamp;
            if (dt <= 0.0) dt = _controlPeriod;
            _lastStamp = p.StampSeconds;

            if (!_designed)
            {
                _pitchLqr = AxisLqr.Design(Clamp(p.SloshPitchOmega), p.SloshDampingRatio,
                    _controlPeriod, _qSloshAngle, _qSloshRate, _qGimbal, _rInput);
                _rollLqr = AxisLqr.Design(Clamp(p.SloshRollOmega), p.SloshDampingRatio,
                    _controlPeriod, _qSloshAngle, _qSloshRate, _qGimbal, _rInput);
                _designed = true;
            }

            var f = p.SpecificForceTankFrame; // slosh-free
            double ffPitch = _ffPitch.Step(TiltGeometry.DesiredPitch(f), dt);
            double ffRoll = _ffRoll.Step(TiltGeometry.DesiredRoll(f), dt);

            double ffRateP = MathD.ClampAbs((ffPitch - _prevFfPitch) / dt, 5.0);
            double ffRateR = MathD.ClampAbs((ffRoll - _prevFfRoll) / dt, 5.0);
            _prevFfPitch = ffPitch;
            _prevFfRoll = ffRoll;

            double pitchCmd = ffRateP + _pitchLqr.Control(
                p.SloshPitchAngle, p.SloshPitchRate, p.PitchAngle - ffPitch);
            double rollCmd = ffRateR + _rollLqr.Control(
                p.SloshRollAngle, p.SloshRollRate, p.RollAngle - ffRoll);

            return StabilizerCommand.Rate(_seq++, s.StampSeconds, pitchCmd, rollCmd);
        }

        private static double Clamp(double omega) => Math.Max(0.5, Math.Min(30.0, omega));
    }
}
