using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;

namespace FluidStabilizer.Rl
{
    /// <summary>
    /// The single place a policy output becomes a stabilizer command. Both the
    /// training environment and the deployed bridge client compose actions through
    /// this, so "what the policy meant" cannot differ between them
    /// (see <see cref="PolicyContract"/>).
    /// </summary>
    public static class ActionComposerFactory
    {
        /// <summary>
        /// Build the composer for a contract against the gimbal actually being
        /// driven. The contract's own max rate must match that gimbal — a policy
        /// trained against a different actuator ceiling means something different
        /// by the same numbers, so this refuses instead of rescaling silently.
        /// </summary>
        public static ActionComposer From(PolicyContract c, GimbalConfig gimbal)
        {
            if (Math.Abs(c.MaxRateRadPerSec - gimbal.MaxRateRadPerSec) > 1e-9)
                throw new InvalidOperationException(
                    $"policy contract max rate {c.MaxRateRadPerSec} != gimbal {gimbal.MaxRateRadPerSec}");
            if (c.ObsVersion != PolicyContract.CurrentObsVersion)
                throw new InvalidOperationException(
                    $"policy observation version {c.ObsVersion} != current {PolicyContract.CurrentObsVersion}");

            return new ActionComposer(
                c.ActionScaleRadPerSec, gimbal.MaxRateRadPerSec,
                PolicyContract.MakeBaseline(c.Baseline),
                c.LockCapable ? new LockGate(gimbal) : LockGate.Disabled);
        }
    }

    /// <summary>
    /// Emits the joint-rate command each control tick. Plain mode:
    /// <c>rate = policy·scale</c>. Residual mode (a baseline is supplied):
    /// <c>rate = baselineRate + policy·scale</c>, clamped to the max rate,
    /// where the baseline's command is converted to an equivalent rate
    /// (angle commands → P-tracked, a locked command → PD-held at zero).
    /// </summary>
    public sealed class ActionComposer : IController
    {
        // Baseline command → rate conversion (rad/s per rad, and per rad/s).
        private const double BaselineTrackGain = 9.0;
        private const double BaselineRateDamp = 0.6;

        private readonly double _rateScale;   // what a policy ±1 maps to (rad/s)
        private readonly double _maxRate;     // hard clamp on the composed rate
        private readonly IController _baseline; // null = plain policy
        private readonly LockGate _lockGate;   // D8 hybrid action; LockGate.Disabled = off

        private double _pitch, _roll;
        private long _seq;

        public ActionComposer(double rateScale, double maxRate, IController baseline, LockGate lockGate = null)
        {
            _rateScale = rateScale;
            _maxRate = maxRate;
            _baseline = baseline;
            _lockGate = lockGate ?? LockGate.Disabled;
        }

        public string Name
        {
            get
            {
                string s = _baseline == null
                    ? "RL policy (joint-rate)"
                    : "RL residual on " + _baseline.Name;
                return _lockGate.Enabled ? s + " (lock-capable)" : s;
            }
        }

        public SensorBundle LastBundle { get; private set; }

        /// <summary>The baseline being corrected, or null in plain mode.</summary>
        public IController BaselineController => _baseline;

        public void Reset()
        {
            _pitch = _roll = 0.0;
            _seq = 0;
            LastBundle = default;
            _lockGate.Reset();
            _baseline?.Reset();
        }

        public void SetAction(double normPitch, double normRoll)
        {
            _pitch = normPitch;
            _roll = normRoll;
        }

        public StabilizerCommand ComputeCommand(in SensorBundle sensors)
        {
            LastBundle = sensors;

            double baseP = 0.0, baseR = 0.0;
            if (_baseline != null)
            {
                StabilizerCommand b = _baseline.ComputeCommand(sensors);
                BaselineRates(b, sensors, out baseP, out baseR);
            }

            double p = Clamp(baseP + _pitch * _rateScale, _maxRate);
            double r = Clamp(baseR + _roll * _rateScale, _maxRate);

            if (_lockGate.ShouldLock(p, r, sensors.Joints.PitchAngle, sensors.Joints.RollAngle))
                return StabilizerCommand.Hold(_seq++, sensors.StampSeconds);

            return StabilizerCommand.Rate(_seq++, sensors.StampSeconds, p, r);
        }

        /// <summary>
        /// The equivalent joint rate of a baseline's command — public so a teacher
        /// trajectory can be converted into the residual this composer would need
        /// to reproduce it (<see cref="BcDatasetExporter"/>).
        /// </summary>
        public static void BaselineRates(in StabilizerCommand b, in SensorBundle s,
            out double pitchRate, out double rollRate)
        {
            switch (b.Mode)
            {
                case StabilizerCommandMode.JointRate:
                    pitchRate = b.PitchCommand;
                    rollRate = b.RollCommand;
                    break;
                case StabilizerCommandMode.JointAngle:
                    pitchRate = BaselineTrackGain * (b.PitchCommand - s.Joints.PitchAngle);
                    rollRate = BaselineTrackGain * (b.RollCommand - s.Joints.RollAngle);
                    break;
                default: // Locked → hold the joint at zero
                    pitchRate = -BaselineTrackGain * s.Joints.PitchAngle - BaselineRateDamp * s.Joints.PitchRate;
                    rollRate = -BaselineTrackGain * s.Joints.RollAngle - BaselineRateDamp * s.Joints.RollRate;
                    break;
            }
        }

        private static double Clamp(double v, double lim) => v < -lim ? -lim : (v > lim ? lim : v);
    }
}
