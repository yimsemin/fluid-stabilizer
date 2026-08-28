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
        private readonly RlActionController _actor;
        private readonly int _controlDecimation;

        private SimulationLoop _loop;
        private double _lastActionPitch, _lastActionRoll;
        private double _lastCumulativeOverflow;

        public RlEnvironment(
            SimulationConfig cfg,
            Func<ulong, IDriverInput> driverFactory,
            Func<ISloshModel> sloshFactory,
            RewardWeights weights,
            int frameStackLength = 6,
            double actionRateScaleRadPerSec = double.NaN)
        {
            cfg.Validate();
            _cfg = cfg;
            _driverFactory = driverFactory ?? throw new ArgumentNullException(nameof(driverFactory));
            _sloshFactory = sloshFactory ?? throw new ArgumentNullException(nameof(sloshFactory));
            _encoder = new ObservationEncoder(frameStackLength);
            _reward = new RewardFunction(weights);
            double scale = double.IsNaN(actionRateScaleRadPerSec)
                ? cfg.Gimbal.MaxRateRadPerSec
                : actionRateScaleRadPerSec;
            _actor = new RlActionController(scale);
            _controlDecimation = cfg.ControlDecimation;
        }

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

        private sealed class RlActionController : IController
        {
            private readonly double _rateScale;
            private double _pitch, _roll;
            private long _seq;

            public RlActionController(double rateScale) { _rateScale = rateScale; }

            public string Name => "RL policy (joint-rate)";
            public SensorBundle LastBundle { get; private set; }

            public void Reset()
            {
                _pitch = _roll = 0.0;
                _seq = 0;
                LastBundle = default;
            }

            public void SetAction(double normPitch, double normRoll)
            {
                _pitch = normPitch;
                _roll = normRoll;
            }

            public StabilizerCommand ComputeCommand(in SensorBundle sensors)
            {
                LastBundle = sensors;
                return StabilizerCommand.Rate(_seq++, sensors.StampSeconds,
                    _pitch * _rateScale, _roll * _rateScale);
            }
        }
    }
}
