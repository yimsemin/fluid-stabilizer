using FluidStabilizer.Core.Sim.Drivers;
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
        private SimulationConfig _cfg;
        private readonly Func<ulong, IDriverInput> _driverFactory;
        private readonly Func<ISloshModel> _sloshFactory;
        private readonly ObservationEncoder _encoder;
        private readonly RewardFunction _reward;
        private ActionComposer _actor;
        private readonly Func<ulong, DriverTrace> _traceSelector;
        private readonly Func<IController> _baselineFactory;
        private readonly double _scaleFraction;
        private readonly bool _lockCapable;
        private readonly string _lockMode;
        public DriverTrace ActiveTrace { get; private set; }
        public SimulationConfig EffectiveConfig => _cfg;
        public ulong EffectiveSeed { get; private set; }
        public Action<RlEnvironment> EpisodeStarted;
        public Action<RlEnvironment> EpisodeFinished;
        private bool _reported;
        private int _controlDecimation;

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
        /// <param name="lockCapable">
        /// Lock-capable hybrid action (decision D8): while the composed rate
        /// command is inside a small dead-band and the joint sits near zero, the
        /// actor emits a mechanically <b>Locked</b> command instead of a sagging
        /// servo hold — so its floor is the rigid tank (≈ 19 L), not the
        /// zero-command servo (≈ 25 L). Closes the F5 structural handicap.</param>
        public RlEnvironment(
            SimulationConfig cfg,
            Func<ulong, IDriverInput> driverFactory,
            Func<ISloshModel> sloshFactory,
            RewardWeights weights,
            int frameStackLength = 6,
            double actionRateScaleRadPerSec = double.NaN,
            Func<IController> baselineFactory = null,
            double residualRateScaleRadPerSec = double.NaN,
            bool lockCapable = false,
            Func<ulong, DriverTrace> traceSelector = null,
            string baselineId = null,
            string lockMode = "deadband")
        {
            _traceSelector = traceSelector;
            _baselineFactory = baselineFactory;
            _lockCapable = lockCapable;
            _lockMode = lockMode;
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

            _scaleFraction = scale / maxRate;
            LockGate gate = lockCapable ? new LockGate(cfg.Gimbal) : LockGate.Disabled;
            Contract = new PolicyContract
            {
                // A contract id (PolicyContract.MakeBaseline understands it), not the
                // controller's display name — the served policy has to rebuild the
                // same baseline from this string.
                Baseline = baseline == null ? "none"
                    : (baselineId ?? throw new ArgumentException(
                        "a residual environment must name its baseline (baselineId) so a served " +
                        "checkpoint can rebuild it", nameof(baselineId))),
                ActionScaleRadPerSec = scale,
                MaxRateRadPerSec = maxRate,
                LockCapable = lockCapable,
                LockMode = lockCapable ? lockMode : "none",
                FrameStack = Math.Max(1, frameStackLength),
                ObsVersion = PolicyContract.CurrentObsVersion,
            };
            _actor = new ActionComposer(scale, maxRate, baseline, gate, Contract.IsExplicitLock);
            _controlDecimation = cfg.ControlDecimation;
        }

        /// <summary>Name of the acting controller (plain policy, or "RL residual on …").</summary>
        public string ActorName => _actor.Name;

        /// <summary>Current gimbal state (telemetry / tests).</summary>
        public GimbalState GimbalState => _loop?.GimbalState ?? default;

        public int ObservationSize => _encoder.StackedSize;
        public int PrivilegedObservationSize => PrivilegedEncoder.Size;
        public int ActionSize => Contract.ActionSize;

        /// <summary>
        /// What this environment means by an action. A checkpoint trained here must
        /// be served under the same contract — see <see cref="PolicyContract"/> and
        /// <see cref="RemotePolicyController"/>.
        /// </summary>
        public PolicyContract Contract { get; private set; }

        /// <summary>The bundle the next control tick will act on (what the composer sees).</summary>
        public SensorBundle NextControlBundle => _loop.LatestSensorBundle;

        /// <summary>The baseline this environment composes on top of, or null in plain mode.</summary>
        public IController Baseline => _actor.BaselineController;
        public double SimTime => _loop?.SimTime ?? 0.0;

        public float[] Reset(ulong seed)
        {
            ActiveTrace = _traceSelector?.Invoke(seed);
            EffectiveSeed = ActiveTrace?.Seed ?? seed;
            if (ActiveTrace != null)
            {
                _cfg = ActiveTrace.ToConfig();
                _controlDecimation = _cfg.ControlDecimation;
                _actor = new ActionComposer(_scaleFraction * _cfg.Gimbal.MaxRateRadPerSec,
                    _cfg.Gimbal.MaxRateRadPerSec, _baselineFactory?.Invoke(),
                    _lockCapable ? new LockGate(_cfg.Gimbal) : LockGate.Disabled,
                    Contract.IsExplicitLock);
            }
            _loop = new SimulationLoop(_cfg, _actor,
                ActiveTrace != null ? new RecordedTraceDriver(ActiveTrace) : _driverFactory(seed),
                ActiveTrace != null ? ActiveTrace.MakeSloshModel() : _sloshFactory());
            _actor.Reset();
            _encoder.Reset();
            _reward.Reset();
            _loop.BeginEpisode(EffectiveSeed);
            _reported = false;
            EpisodeStarted?.Invoke(this);

            _lastActionPitch = _lastActionRoll = 0.0;
            _lastCumulativeOverflow = 0.0;

            // The bundle primed at t = 0; no control tick has run yet.
            return _encoder.Observe(_loop.LatestSensorBundle, 0.0, 0.0, _actor.IsLocked);
        }

        public RlStepResult Step(float actionPitch, float actionRoll) => Step(actionPitch, actionRoll, 0f);

        /// <param name="actionLock">
        /// Explicit-lock contracts only (findings F9): &gt; 0 requests the mechanical
        /// lock for this control tick. Ignored under a dead-band contract.</param>
        public RlStepResult Step(float actionPitch, float actionRoll, float actionLock)
        {
            if (_loop == null) throw new InvalidOperationException("Reset() before Step().");

            double ap = Clamp1(actionPitch);
            double ar = Clamp1(actionRoll);
            double al = Clamp1(actionLock);
            bool lockedBefore = _actor.IsLocked;
            _actor.SetAction(ap, ar, al);

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
                // A lock/unlock toggle costs the same as a full-scale action change:
                // engaging and releasing the mechanism is a real physical event, not
                // a free switch, so the policy should not chatter it.
                ActionRateL1 = Math.Abs(ap - _lastActionPitch) + Math.Abs(ar - _lastActionRoll)
                               + (_actor.IsLocked != lockedBefore ? 1.0 : 0.0),
                NormalizedTorqueEffort = (Math.Abs(g.Pitch.AppliedTorque) + Math.Abs(g.Roll.AppliedTorque)) / peakTorque,
                PitchAngleFraction = Math.Abs(g.Pitch.Angle) / maxAngle,
                RollAngleFraction = Math.Abs(g.Roll.Angle) / maxAngle,
                TankTiltSq = g.Pitch.Angle * g.Pitch.Angle + g.Roll.Angle * g.Roll.Angle,
                CumulativeOverflow = gt.CumulativeOverflowVolume,
            };

            (double reward, bool terminated, RewardBreakdown breakdown) = _reward.Step(ctx);

            // The bundle the *next* control tick will see, paired with the action
            // just applied — the same (state, previous action) pairing the deployed
            // RemotePolicyController builds. Using _actor.LastBundle here paired the
            // action with the state one control tick older than deployment did, so a
            // policy trained on it saw a differently-aligned observation than the one
            // it is served (docs/rl.md "행동 계약").
            float[] obs = _encoder.Observe(_loop.LatestSensorBundle, ap, ar, _actor.IsLocked);
            _lastActionPitch = ap;
            _lastActionRoll = ar;

            if (!_reported && (terminated || _loop.EpisodeComplete))
            {
                _reported = true;
                EpisodeFinished?.Invoke(this);
            }
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

    }
}
