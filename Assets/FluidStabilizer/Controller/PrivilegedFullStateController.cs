using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// Performance-ceiling baseline (design-review §2, §12).
    ///
    /// Key finding from the plant model: the gimbal's angular acceleration forces
    /// the slosh pendulum almost 1:1 (the Euler term α×r in the pendulum
    /// equation), so any controller that moves the tank abruptly to "help"
    /// actually kicks the liquid and can drive the slosh mode into resonance.
    /// A working controller must move the tank <em>smoothly</em>.
    ///
    /// This ceiling therefore tracks the slosh-free effective-gravity tilt with a
    /// smoothed, slew-limited command and only a light slosh-rate damping term.
    /// It is hand-tuned, deliberately conservative, and represents "best smooth
    /// feed-forward + gentle feedback with perfect state" rather than a solved
    /// optimal controller (LQR/MPC ceiling is phase-2). Never deployed.
    /// </summary>
    public sealed class PrivilegedFullStateController : IPrivilegedController
    {
        private readonly double _kTrack;      // proportional pull toward the smoothed tilt target
        private readonly double _kSloshRate;  // light slosh-mode damping
        private readonly double _maxSlewRadPerSec2; // limit on how fast the rate command may change
        private readonly LowPass _ffPitch, _ffRoll;
        private double _cmdPitch, _cmdRoll;
        private long _seq;
        private double _lastStamp;

        public PrivilegedFullStateController(
            double kTrack = 1.0, double kSloshRate = 0.0, double maxSlew = 20.0)
        {
            _kTrack = kTrack;
            _kSloshRate = kSloshRate;
            _maxSlewRadPerSec2 = maxSlew;
            _ffPitch = new LowPass(1.6);
            _ffRoll = new LowPass(1.6);
        }

        public string Name => "PrivilegedFullState (ceiling)";

        public void Reset()
        {
            _ffPitch.Reset();
            _ffRoll.Reset();
            _cmdPitch = _cmdRoll = 0.0;
            _seq = 0;
            _lastStamp = 0.0;
        }

        public StabilizerCommand ComputeCommand(in SensorBundle s)
            => StabilizerCommand.Hold(_seq++, s.StampSeconds);

        public StabilizerCommand ComputeCommand(in SensorBundle s, in PrivilegedObservation p)
        {
            double dt = p.StampSeconds - _lastStamp;
            if (dt <= 0.0) dt = 0.01;
            _lastStamp = p.StampSeconds;

            var f = p.SpecificForceTankFrame; // slosh-free
            double targetPitch = _ffPitch.Step(Math.Atan2(f.X, Math.Max(1e-6, f.Z)), dt);
            double targetRoll = _ffRoll.Step(Math.Atan2(f.Y, Math.Max(1e-6, f.Z)), dt);

            double rawPitch = _kTrack * (targetPitch - p.PitchAngle) - _kSloshRate * p.SloshPitchRate;
            double rawRoll = _kTrack * (targetRoll - p.RollAngle) - _kSloshRate * p.SloshRollRate;

            _cmdPitch = MathD.MoveToward(_cmdPitch, rawPitch, _maxSlewRadPerSec2 * dt);
            _cmdRoll = MathD.MoveToward(_cmdRoll, rawRoll, _maxSlewRadPerSec2 * dt);

            return new StabilizerCommand
            {
                Sequence = _seq++,
                FromStampSeconds = s.StampSeconds,
                PitchRateCommand = _cmdPitch,
                RollRateCommand = _cmdRoll
            };
        }
    }
}
