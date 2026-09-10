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
                c.LockCapable ? new LockGate(gimbal) : LockGate.Disabled,
                c.IsExplicitLock);
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
        private readonly bool _explicitLock;   // the policy requests the lock itself (F9)

        private double _pitch, _roll, _lockRequest;
        private double _releaseIntent;   // low-passed release request (explicit mode)
        private long _seq;

        public ActionComposer(double rateScale, double maxRate, IController baseline,
            LockGate lockGate = null, bool explicitLock = false)
        {
            _rateScale = rateScale;
            _maxRate = maxRate;
            _baseline = baseline;
            _lockGate = lockGate ?? LockGate.Disabled;
            _explicitLock = explicitLock;
        }

        public string Name
        {
            get
            {
                string s = _baseline == null
                    ? "RL policy (joint-rate)"
                    : "RL residual on " + _baseline.Name;
                if (!_lockGate.Enabled) return s;
                return s + (_explicitLock ? " (lock: policy-chosen)" : " (lock: dead-band)");
            }
        }

        public SensorBundle LastBundle { get; private set; }

        /// <summary>The baseline being corrected, or null in plain mode.</summary>
        public IController BaselineController => _baseline;

        public void Reset()
        {
            _pitch = _roll = _lockRequest = _releaseIntent = 0.0;
            _seq = 0;
            LastBundle = default;
            // Explicit mode starts locked: the rigid tank is the default state, and
            // it is the only state the policy can return to from anywhere.
            _lockGate.Reset(_explicitLock);
            _baseline?.Reset();
        }

        /// <summary>
        /// The pending action. <paramref name="lockRequest"/> is only read in
        /// explicit-lock mode (findings F9), where it is a <b>release</b> request:
        /// &gt; 0 hands the joint to the active servo, anything else keeps (or
        /// returns to) the mechanical lock.
        ///
        /// The sign is deliberate. The rigid tank is the best analytic result we
        /// have, so it must be what a policy gets for free: an untrained network
        /// outputs ≈ 0 and therefore stays locked, and only a committed positive
        /// output takes over. Under a dead-band contract this argument is ignored.
        /// </summary>
        public void SetAction(double normPitch, double normRoll, double lockRequest = 0.0)
        {
            _pitch = normPitch;
            _roll = normRoll;
            _lockRequest = lockRequest;
        }

        /// <summary>True while the joint is mechanically locked (fed to the observation).</summary>
        public bool IsLocked => _lockGate.IsLocked;

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

            // Explicit mode: the policy states the intent and only the physical
            // constraints remain (findings F9). Dead-band mode: the gate infers it.
            // A mechanical lock is not re-decided at the control rate. The release
            // request is low-passed into a sustained intent, so a stochastic policy's
            // per-tick noise averages out — zero-mean sampling never reaches the
            // threshold — and only a committed output releases the joint. Without
            // this an episode is ~2500 independent draws, and even a policy whose
            // mean says "stay locked" releases almost immediately (findings F9).
            _releaseIntent += ReleaseIntentGain * (_lockRequest - _releaseIntent);
            bool locked = _explicitLock
                ? _lockGate.ShouldLockExplicit(_releaseIntent <= ReleaseThreshold,
                    sensors.Joints.PitchAngle, sensors.Joints.RollAngle)
                : _lockGate.ShouldLock(p, r, sensors.Joints.PitchAngle, sensors.Joints.RollAngle);

            if (locked)
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

        /// <summary>First-order gain on the release intent (≈ 10 control ticks).</summary>
        private const double ReleaseIntentGain = 0.1;

        /// <summary>Sustained intent needed to release the lock.</summary>
        private const double ReleaseThreshold = 0.5;

        private static double Clamp(double v, double lim) => v < -lim ? -lim : (v > lim ? lim : v);
    }
}
