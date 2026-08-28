using System;

namespace FluidStabilizer.Rl
{
    /// <summary>
    /// Per-step reward weights (design-review §11). All terms are costs (the
    /// weighted quantity is subtracted) except <see cref="AliveBonus"/>. Defaults
    /// are a starting point for curriculum tuning, not sacred.
    /// </summary>
    [Serializable]
    public struct RewardWeights
    {
        public double Overflow;            // k1 · overflow volume this step (m³)   — primary
        public double NearRim;             // k2 · (rim-margin shortfall)²          — smooth near-rim cost
        public double SloshEnergy;         // k3 · slosh kinetic energy (J)
        public double ActionRate;          // k4 · Σ|Δaction|
        public double ActuatorEffort;      // k5 · Σ|torque| / peak torque
        public double JointLimitProximity; // k6 · Σ proximity²  (per axis, near the travel limit)
        public double TankTilt;            // k7 · (pitch² + roll²)   — discourage wild gymnastics
        public double AliveBonus;          // k8 · 1 (added)
        public double TerminalSpill;       // K_term subtracted once when cumulative spill crosses the threshold

        /// <summary>Freeboard (m) at which the near-rim cost starts ramping in.</summary>
        public double NearRimMargin;
        /// <summary>Cumulative spill (m³) that ends the episode as a "large spill" failure.</summary>
        public double TerminalSpillVolume;
        /// <summary>|joint angle| / max as a fraction, above which the limit-proximity cost starts.</summary>
        public double JointLimitProximityStart;

        public static RewardWeights Default() => new RewardWeights
        {
            Overflow = 500.0,
            NearRim = 0.05,
            SloshEnergy = 1.0e-3,
            ActionRate = 0.02,
            ActuatorEffort = 0.01,
            JointLimitProximity = 0.05,
            TankTilt = 5.0e-3,
            AliveBonus = 0.0,
            TerminalSpill = 5.0,
            NearRimMargin = 0.10,
            TerminalSpillVolume = 0.05,
            JointLimitProximityStart = 0.8,
        };
    }

    /// <summary>Privileged quantities the environment feeds the reward each control step.</summary>
    public struct RewardContext
    {
        public double OverflowVolumeThisStep;   // m³
        public double MinFreeboard;             // m (negative while overflowing)
        public double SloshKineticEnergy;       // J
        public double ActionRateL1;             // Σ|action − prevAction| over the 2 axes
        public double NormalizedTorqueEffort;   // (|τpitch| + |τroll|) / peak torque
        public double PitchAngleFraction;       // |pitch| / maxAngle
        public double RollAngleFraction;        // |roll|  / maxAngle
        public double TankTiltSq;               // pitch² + roll²  (rad²)
        public double CumulativeOverflow;       // m³
    }

    /// <summary>Per-term contributions to one step's reward, for logging and reward-hacking watch.</summary>
    public struct RewardBreakdown
    {
        public double Overflow, NearRim, SloshEnergy, ActionRate, ActuatorEffort,
            JointLimitProximity, TankTilt, AliveBonus, TerminalSpill;
        public double Total;
    }

    /// <summary>
    /// The continuous per-step reward (design-review §11). Stateful only for the
    /// one-shot terminal-spill penalty; call <see cref="Reset"/> each episode.
    /// </summary>
    public sealed class RewardFunction
    {
        private readonly RewardWeights _w;
        private bool _terminalFired;

        public RewardFunction(RewardWeights weights) { _w = weights; }

        public void Reset() { _terminalFired = false; }

        public (double reward, bool terminated, RewardBreakdown breakdown) Step(in RewardContext c)
        {
            var b = new RewardBreakdown();

            b.Overflow = -_w.Overflow * Math.Max(0.0, c.OverflowVolumeThisStep);

            double shortfall = Math.Max(0.0, _w.NearRimMargin - c.MinFreeboard);
            double s = _w.NearRimMargin > 1e-9 ? shortfall / _w.NearRimMargin : 0.0;
            b.NearRim = -_w.NearRim * s * s;

            b.SloshEnergy = -_w.SloshEnergy * Math.Max(0.0, c.SloshKineticEnergy);
            b.ActionRate = -_w.ActionRate * Math.Max(0.0, c.ActionRateL1);
            b.ActuatorEffort = -_w.ActuatorEffort * Math.Max(0.0, c.NormalizedTorqueEffort);

            b.JointLimitProximity = -_w.JointLimitProximity *
                (LimitCost(c.PitchAngleFraction) + LimitCost(c.RollAngleFraction));

            b.TankTilt = -_w.TankTilt * Math.Max(0.0, c.TankTiltSq);
            b.AliveBonus = _w.AliveBonus;

            bool terminated = false;
            if (!_terminalFired && c.CumulativeOverflow >= _w.TerminalSpillVolume && _w.TerminalSpillVolume > 0.0)
            {
                _terminalFired = true;
                terminated = true;
                b.TerminalSpill = -_w.TerminalSpill;
            }

            b.Total = b.Overflow + b.NearRim + b.SloshEnergy + b.ActionRate + b.ActuatorEffort
                      + b.JointLimitProximity + b.TankTilt + b.AliveBonus + b.TerminalSpill;
            return (b.Total, terminated, b);
        }

        private double LimitCost(double angleFraction)
        {
            double start = _w.JointLimitProximityStart;
            if (angleFraction <= start || start >= 1.0) return 0.0;
            double x = (angleFraction - start) / (1.0 - start);
            return x * x;
        }
    }
}
