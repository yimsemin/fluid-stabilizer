using System;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>
    /// The lock-capable hybrid action (decision D8, 묶음 3). An active controller's
    /// "hold" is a sagging position servo — the slosh moment drags it off target
    /// and it loses ≈ 5–7 L to a mechanically locked joint even when it commands
    /// zero (finding F5). This gate lets the actor <em>be</em> the rigid tank when
    /// it has nothing useful to do: while the composed active rate command stays
    /// inside a small dead-band and the joint sits near zero, emit a
    /// <see cref="Contracts.StabilizerCommandMode.Locked"/> command instead — the
    /// vehicle structure takes the moment, no torque is consumed, the winding
    /// cools. It unlocks only when the command demands a real correction.
    ///
    /// Hysteresis (<see cref="_unlockRate"/> &gt; <see cref="_lockRate"/>) plus a
    /// minimum lock dwell stop lock/active chatter at the control rate. Locking is
    /// gated on a near-zero joint angle so the actuator's instantaneous snap to
    /// zero in <c>GimbalActuator.LockAxis</c> is negligible; under a load the servo
    /// cannot null out, the gate simply stays in the active branch.
    ///
    /// Pure deterministic state machine — no RNG, no wall-clock. One instance per
    /// episode/rollout, <see cref="Reset"/> at the start.
    /// </summary>
    public sealed class LockGate
    {
        private readonly bool _enabled;
        private readonly double _lockRate;    // rad/s — below this (and near zero angle) → lock
        private readonly double _unlockRate;  // rad/s — must exceed this to leave the lock
        private readonly double _lockAngle;   // rad   — only lock when both joints are within this of zero
        private readonly int _minDwellTicks;  // control ticks a lock must persist before it can release

        private bool _locked;
        private int _ticksSinceLock;

        /// <summary>Disabled gate — <see cref="ShouldLock"/> is always false (the pre-D8 behaviour).</summary>
        public static readonly LockGate Disabled = new LockGate();

        private LockGate()
        {
            _enabled = false;
        }

        public LockGate(GimbalConfig g)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            _enabled = true;
            _lockRate = Math.Max(0.0, g.LockRateThresholdRadPerSec);
            _unlockRate = _lockRate * Math.Max(1.0, g.UnlockRateFactor);
            _lockAngle = Math.Max(0.0, g.LockAngleThresholdRad);
            _minDwellTicks = Math.Max(0, g.MinLockDwellTicks);
        }

        public bool Enabled => _enabled;

        /// <summary>True while the gate is currently holding the joint mechanically locked.</summary>
        public bool IsLocked => _enabled && _locked;

        public void Reset()
        {
            _locked = false;
            _ticksSinceLock = 0;
        }

        /// <summary>
        /// Advance the gate one control tick from an <b>explicit</b> lock request
        /// (findings F9). The dead-band variant below decides for the policy, which
        /// makes the action space a cliff: inside the band the action has no effect
        /// at all, past it the servo takes over and spill jumps ~2.4x, and there is
        /// no gradient between for a policy to follow. Here the policy states its
        /// intent and only the <i>physical</i> constraints remain — the lock can
        /// still be engaged only near zero angle (the actuator snaps to zero) and
        /// must persist <see cref="_minDwellTicks"/> before releasing, so the
        /// mechanism cannot chatter at the control rate.
        /// </summary>
        public bool ShouldLockExplicit(bool request, double pitchAngle, double rollAngle)
        {
            if (!_enabled) return false;

            double angle = Math.Max(Math.Abs(pitchAngle), Math.Abs(rollAngle));

            if (_locked)
            {
                _ticksSinceLock++;
                if (!request && _ticksSinceLock >= _minDwellTicks) _locked = false;
            }
            else if (request && angle < _lockAngle)
            {
                _locked = true;
                _ticksSinceLock = 0;
            }

            return _locked;
        }

        /// <summary>
        /// Advance the gate one control tick. <paramref name="pitchRate"/> /
        /// <paramref name="rollRate"/> are the composed active rate command (rad/s)
        /// the controller would otherwise emit; <paramref name="pitchAngle"/> /
        /// <paramref name="rollAngle"/> are the current joint angles (rad).
        /// Returns true if the controller should emit a Locked command instead.
        /// </summary>
        public bool ShouldLock(double pitchRate, double rollRate, double pitchAngle, double rollAngle)
        {
            if (!_enabled) return false;

            double demand = Math.Max(Math.Abs(pitchRate), Math.Abs(rollRate));
            double angle = Math.Max(Math.Abs(pitchAngle), Math.Abs(rollAngle));

            if (_locked)
            {
                _ticksSinceLock++;
                if (demand > _unlockRate && _ticksSinceLock >= _minDwellTicks)
                    _locked = false;
            }
            else if (demand < _lockRate && angle < _lockAngle)
            {
                _locked = true;
                _ticksSinceLock = 0;
            }

            return _locked;
        }
    }
}
