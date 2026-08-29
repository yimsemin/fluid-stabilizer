using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;

namespace FluidStabilizer.Rl
{
    /// <summary>Outcome of one <see cref="RlEnvironment.Step"/> — a Gymnasium-style tuple.</summary>
    public struct RlStepResult
    {
        public float[] Observation;              // actor: stacked sensor frames, ~unit scale
        public double Reward;
        public bool Terminated;                  // a large-spill failure (design-review §11 K_term)
        public bool Truncated;                   // episode time limit reached
        public float[] PrivilegedObservation;    // critic only — never given to the actor
        public RewardBreakdown RewardBreakdown;  // per-term, for logging / reward-hacking watch
    }

    /// <summary>
    /// A deterministic Gymnasium-style environment around <see cref="SimulationLoop"/>:
    /// <c>Reset(seed) → obs</c>, <c>Step(action) → (obs, reward, terminated, truncated, info)</c>.
    /// The actor sees only <see cref="ObservationEncoder"/> output (sensor-derived,
    /// noisy, delayed, frame-stacked); the critic additionally gets
    /// <see cref="PrivilegedObservation"/>. Action is a per-axis joint
    /// angular-rate command in [-1, 1] (design-review §11), scaled to rad/s.
    ///
    /// Engine-free: the socket bridge for an external Python trainer wraps this;
    /// it holds no Unity types so a trainer needs no Unity install.
    /// </summary>
    public sealed class RlEnvironment
    {
        private readonly SimulationConfig _cfg;
        private readonly Func<ulong, IDriverInput> _driverFactory;
        private readonly Func<ISloshModel> _sloshFactory;
        private readonly ObservationEncoder _encoder;
        private readonly RewardFunction _reward;
        private readonly ResidualActionController _actor;
        private readonly int _controlDecimation;

        private SimulationLoop _loop;
        private double _lastActionPitch, _lastActionRoll;
        private double _lastCumulativeOverflow;

        /// <param name="actionRateScaleRadPerSec">
        /// What a policy output of ±1 maps to, in rad/s, when there is <b>no</b>
        /// baseline. Default = <see cref="GimbalConfig.MaxRateRadPerSec"/>.</param>
        /// <param name="baselineFactory">
        /// Optional inner controller. When set, the env runs in <b>residual</b>
        /// mode: each control tick the composed joint-rate command is
        /// <c>baseline + policy·residualScale</c> (both clamped to the max rate).
        /// The policy only has to learn a small correction on top of a sane
        /// operating point — the design-review fix for the F1/F4 exploration
        /// cliff (docs/rl.md, docs/experiment-plan.md step 4).</param>
        /// <param name="residualRateScaleRadPerSec">
        /// What a policy output of ±1 maps to in residual mode. Default =
        /// 0.35·<see cref="GimbalConfig.MaxRateRadPerSec"/>.</param>
        public RlEnvironment(
            SimulationConfig cfg,
            Func<ulong, IDriverInput> driverFactory,
            Func<ISloshModel> sloshFactory,
            RewardWeights weights,
            int frameStackLength = 6,
            double actionRateScaleRadPerSec = double.NaN,
            Func<IController> baselineFactory = null,
            double residualRateScaleRadPerSec = double.NaN)
        {
            cfg.Validate();
            _cfg = cfg;
            _driverFactory = driverFactory ?? throw new ArgumentNullException(nameof(driverFactory));
            _sloshFactory = sloshFactory ?? throw new ArgumentNullException(nameof(sloshFactory));
            _encoder = new ObservationEncoder(frameStackLength);
            _reward = new RewardFunction(weights);

            double maxRate = cfg.Gimbal.MaxRateRadPerSec;
            IController baseline = baselineFactory?.Invoke();
            double scale = baseline == null
                ? (double.IsNaN(actionRateScaleRadPerSec) ? maxRate : actionRateScaleRadPerSec)
                : (double.IsNaN(residualRateScaleRadPerSec) ? 0.35 * maxRate : residualRateScaleRadPerSec);

            _actor = new ResidualActionController(scale, maxRate, baseline);
            _controlDecimation = cfg.ControlDecimation;
        }

        /// <summary>Name of the acting controller (plain policy, or "RL residual on …").</summary>
        public string ActorName => _actor.Name;

        /// <summary>Current gimbal state (telemetry / tests).</summary>
        public GimbalState GimbalState => _loop?.GimbalState ?? default;

        public int ObservationSize => _encoder.StackedSize;
        public int PrivilegedObservationSize => PrivilegedEncoder.Size;
        public int ActionSize => 2;
        public double SimTime => _loop?.SimTime ?? 0.0;

        public float[] Reset(ulong seed)
        {
            _loop = new SimulationLoop(_cfg, _actor, _driverFactory(seed), _sloshFactory());
            _actor.Reset();
            _encoder.Reset();
            _reward.Reset();
            _loop.BeginEpisode(seed);

            _lastActionPitch = _lastActionRoll = 0.0;
            _lastCumulativeOverflow = 0.0;

            // The bundle primed at t = 0; no control tick has run yet.
            return _encoder.Observe(_loop.LatestSensorBundle, 0.0, 0.0);
        }

        public RlStepResult Step(float actionPitch, float actionRoll)
        {
            if (_loop == null) throw new InvalidOperationException("Reset() before Step().");

            double ap = Clamp1(actionPitch);
            double ar = Clamp1(actionRoll);
            _actor.SetAction(ap, ar);

            double minFreeboard = double.PositiveInfinity;
            for (int i = 0; i < _controlDecimation && _loop.StepPhysics(); i++)
                minFreeboard = Math.Min(minFreeboard, _loop.LatestGroundTruth.MinFreeboard);

            GroundTruthFrame gt = _loop.LatestGroundTruth;
            GimbalState g = _loop.GimbalState;
            SloshGroundTruth slosh = _loop.SloshModel.SampleGroundTruth();

            double overflowThisStep = Math.Max(0.0, gt.CumulativeOverflowVolume - _lastCumulativeOverflow);
            _lastCumulativeOverflow = gt.CumulativeOverflowVolume;
            if (double.IsPositiveInfinity(minFreeboard)) minFreeboard = gt.MinFreeboard;

            double maxAngle = Math.Max(1e-6, _cfg.Gimbal.MaxAngleRad);
            double peakTorque = Math.Max(1e-6, _cfg.Gimbal.MaxTorqueNm);

            var ctx = new RewardContext
            {
                OverflowVolumeThisStep = overflowThisStep,
                MinFreeboard = minFreeboard,
                SloshKineticEnergy = slosh.SloshKineticEnergy,
                ActionRateL1 = Math.Abs(ap - _lastActionPitch) + Math.Abs(ar - _lastActionRoll),
                NormalizedTorqueEffort = (Math.Abs(g.Pitch.AppliedTorque) + Math.Abs(g.Roll.AppliedTorque)) / peakTorque,
                PitchAngleFraction = Math.Abs(g.Pitch.Angle) / maxAngle,
                RollAngleFraction = Math.Abs(g.Roll.Angle) / maxAngle,
                TankTiltSq = g.Pitch.Angle * g.Pitch.Angle + g.Roll.Angle * g.Roll.Angle,
                CumulativeOverflow = gt.CumulativeOverflowVolume,
            };

            (double reward, bool terminated, RewardBreakdown breakdown) = _reward.Step(ctx);

            float[] obs = _encoder.Observe(_actor.LastBundle, ap, ar);
            _lastActionPitch = ap;
            _lastActionRoll = ar;

            return new RlStepResult
            {
                Observation = obs,
                Reward = reward,
                Terminated = terminated,
                Truncated = _loop.EpisodeComplete,
                PrivilegedObservation = PrivilegedEncoder.Encode(_loop.BuildPrivilegedObservation()),
                RewardBreakdown = breakdown,
            };
        }

        /// <summary>The full episode roll-up (same metrics the analytic baselines are scored on).</summary>
        public EpisodeResult Summary() => _loop.Result();

        private static double Clamp1(double v) => v < -1.0 ? -1.0 : (v > 1.0 ? 1.0 : v);

        // ---- the controller seam the loop drives; turns the pending action into a command ----

        /// <summary>
        /// Emits the joint-rate command each control tick. Plain mode:
        /// <c>rate = policy·scale</c>. Residual mode (a baseline is supplied):
        /// <c>rate = baselineRate + policy·scale</c>, clamped to the max rate,
        /// where the baseline's command is converted to an equivalent rate
        /// (angle commands → P-tracked, a locked command → PD-held at zero).
        /// </summary>
        private sealed class ResidualActionController : IController
        {
            // Baseline command → rate conversion (rad/s per rad, and per rad/s).
            private const double BaselineTrackGain = 9.0;
            private const double BaselineRateDamp = 0.6;

            private readonly double _rateScale;   // what a policy ±1 maps to (rad/s)
            private readonly double _maxRate;     // hard clamp on the composed rate
            private readonly IController _baseline; // null = plain policy

            private double _pitch, _roll;
            private long _seq;

            public ResidualActionController(double rateScale, double maxRate, IController baseline)
            {
                _rateScale = rateScale;
                _maxRate = maxRate;
                _baseline = baseline;
            }

            public string Name => _baseline == null
                ? "RL policy (joint-rate)"
                : "RL residual on " + _baseline.Name;

            public SensorBundle LastBundle { get; private set; }

            public void Reset()
            {
                _pitch = _roll = 0.0;
                _seq = 0;
                LastBundle = default;
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
                return StabilizerCommand.Rate(_seq++, sensors.StampSeconds, p, r);
            }

            private static void BaselineRates(in StabilizerCommand b, in SensorBundle s,
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
}
