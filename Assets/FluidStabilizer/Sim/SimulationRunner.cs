using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;
using FluidStabilizer.Rl;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    public enum StabilizerChoice
    {
        Off,                    // tank held rigid to the bed (HoldController)
        SpecificForceAlignment,
        SpecificForceAlignmentPlusI,
        PrivilegedFeedforward,  // cheats (true slosh-free tilt) — for comparison only
        LearnedPolicy,          // ③ — the RL policy, served by python/serve_policy.py over TCP
        HindsightCeiling,       // ④ — offline optimum replayed; needs <trace>.traj.csv beside the trace
        LearnedPolicyB,         // comparison-only: a second learned-policy lane on ComparisonRunner.PolicyPortB
                                 // (the "새 정책" side of the old-vs-new live-training comparison) — never
                                 // reachable from the single-sim 0-5 keys, only from ComparisonRunner.
    }

    public enum SloshModelChoice
    {
        PendulumRom,    // 1 pendulum / axis — the cheap default (PendulumSloshModel)
        ShallowWater,   // 2-D Saint-Venant height field (ShallowWaterSloshModel)
    }

    /// <summary>
    /// Drives a <see cref="SimulationLoop"/> in real time for interactive play.
    /// The loop and every model it owns are engine-free; this MonoBehaviour only
    /// paces it and exposes state to the view / HUD.
    /// </summary>
    public sealed class SimulationRunner : MonoBehaviour
    {
        [Header("Setup")]
        [Tooltip("Read fill / road / slosh model / seed from experiment.txt at the repo root. " +
                 "Uncheck to use the Inspector values below instead.")]
        public bool UseExperimentProfile = true;
        // 0.62: above the pendulum ROM's spill threshold (~0.56, its wave cap is
        // 0.8·depth), so the play scene actually spills under hard driving.
        public double FillRatio = 0.62;
        public StabilizerChoice Stabilizer = StabilizerChoice.Off;
        public SloshModelChoice SloshModel = SloshModelChoice.PendulumRom;
        [Tooltip("Rolling grade, curve bank, surface roughness and the odd pothole. Off = perfectly flat road.")]
        public bool Road = false;
        [Range(0f, 2f)] public float RoadSeverity = 1f;
        [Range(0.05f, 1f)] public float TimeScale = 1f;
        public ulong Seed = 1;

        [Header("Learned policy (③) — python/serve_policy.py")]
        public string PolicyHost = "127.0.0.1";
        public int PolicyPort = 5601;
        [Tooltip("Frame-stack length the policy was trained with (RlServerLauncher -rlStack).")]
        public int PolicyFrameStack = 6;

        [Header("Runtime (read-only)")]
        [SerializeField] private double _simTime;
        [SerializeField] private double _speedKmh;
        [SerializeField] private double _minFreeboard;
        [SerializeField] private double _spillLitres;

        /// <summary>Tooling/test hook: drive with this instead of keyboard/gamepad. Set before BuildLoop.</summary>
        public IDriverInput DriverOverride;
        public FluidStabilizer.Core.Sim.Drivers.DriverTrace ReplayTrace;
        public ulong ActiveSeed { get; private set; }

        public SimulationLoop Loop { get; private set; }
        public HumanDriverInput Driver { get; private set; }
        public StabilizerChoice ActiveStabilizer { get; private set; }
        public SloshModelChoice ActiveSloshModel { get; private set; }

        /// <summary>The <c>experiment.txt</c> profile the current loop was built from (null when the Inspector fields are used instead).</summary>
        public ExperimentProfile ActiveProfile { get; private set; }

        private double _accumulator;
        private double _tankHeightZ = 0.60;
        private IController _built;   // tracked so an IDisposable controller (learned policy socket) is released
        private const int MaxStepsPerFrame = 40;

        /// <summary>
        /// Build the controller for a choice. Analytic ones delegate to the
        /// static <see cref="MakeController(StabilizerChoice)"/>; the learned
        /// policy needs this instance's host/port and is disposed on swap.
        /// </summary>
        private IController BuildController(StabilizerChoice choice)
        {
            if (_built is IDisposable d) { try { d.Dispose(); } catch { } }

            if (choice == StabilizerChoice.LearnedPolicy)
            {
                SimulationConfig active = ReplayTrace?.ToConfig() ?? Loop?.Config ?? new SimulationConfig();
                _built = new RemotePolicyController(PolicyHost, PolicyPort, Mathf.Max(1, PolicyFrameStack),
                    active.Gimbal.MaxRateRadPerSec, log: s => Debug.Log(s), gimbal: active.Gimbal);
            }
            else if (choice == StabilizerChoice.HindsightCeiling)
                _built = MakeHindsightCeiling(ReplayTrace) ?? new HoldController();
            else
                _built = MakeController(choice);
            return _built;
        }

        private void OnDestroy()
        {
            if (_built is IDisposable d) { try { d.Dispose(); } catch { } }
        }

        /// <summary>Copy the shared <c>experiment.txt</c> knobs onto the Inspector fields, so the play scene follows the file.</summary>
        private void ApplyProfile(ExperimentProfile p)
        {
            FillRatio = p.FillRatio;
            _tankHeightZ = p.TankHeightZ;
            Road = p.RoadEnabled;
            RoadSeverity = (float)p.RoadSeverity;
            Seed = p.Seed;
            SloshModel = p.SloshModel == "shallow" ? SloshModelChoice.ShallowWater : SloshModelChoice.PendulumRom;
        }

        private void Awake() => BuildLoop();

        public void BuildLoop()
        {
            // Follow experiment.txt in the actual play scene; leave direct
            // BuildLoop() callers (tooling, tests) on their explicit values.
            ActiveProfile = (ReplayTrace == null && UseExperimentProfile && Application.isPlaying) ? ExperimentProfileStore.Load() : null;

            SimulationConfig cfg;
            if (ReplayTrace != null)
            {
                cfg = ReplayTrace.ToConfig();
                Seed = ReplayTrace.Seed;
                SloshModel = ReplayTrace.SloshModel == "shallow" ? SloshModelChoice.ShallowWater : SloshModelChoice.PendulumRom;
            }
            else if (ActiveProfile != null)
            {
                cfg = ActiveProfile.ToConfig(0.0);   // 0 = endless
                ApplyProfile(ActiveProfile);         // sync the read-only Inspector fields
            }
            else
            {
                cfg = new SimulationConfig { EpisodeSeconds = 0.0 };
                cfg.Slosh.FillRatio = FillRatio;
                cfg.Slosh.TankHeightZ = _tankHeightZ;
                cfg.Road.Enabled = Road;
                cfg.Road.Severity = RoadSeverity;
            }

            Driver = new HumanDriverInput();
            Loop = new SimulationLoop(cfg, BuildController(Stabilizer),
                ReplayTrace != null ? new FluidStabilizer.Core.Sim.Drivers.RecordedTraceDriver(ReplayTrace) : DriverOverride ?? Driver, MakeSlosh(SloshModel));
            ActiveSeed = ReplayTrace?.Seed ?? Seed;
            Loop.BeginEpisode(ActiveSeed);
            ActiveStabilizer = Stabilizer;
            ActiveSloshModel = SloshModel;
            _accumulator = 0;
        }

        public void ResetEpisode()
        {
            Loop.SetController(BuildController(ActiveStabilizer));
            ActiveSeed = ReplayTrace?.Seed ?? Seed;
            Loop.BeginEpisode(ActiveSeed);
            _accumulator = 0;
        }

        /// <summary>
        /// Swap the slosh model live. The loop takes its model at construction,
        /// so this rebuilds the loop and restarts the episode.
        /// </summary>
        public void SetSloshModel(SloshModelChoice choice)
        {
            SloshModel = choice;
            BuildLoop();
        }

        /// <summary>Toggle the road profile live. Rebuilds the loop and restarts the episode.</summary>
        public void SetRoad(bool enabled)
        {
            Road = enabled;
            BuildLoop();
        }

        public void SetStabilizer(StabilizerChoice choice)
        {
            ActiveStabilizer = choice;
            Stabilizer = choice;
            Loop.SetController(BuildController(choice));
        }

        public static IController MakeController(StabilizerChoice c) => c switch
        {
            StabilizerChoice.SpecificForceAlignment => new SpecificForceAlignmentController(),
            StabilizerChoice.SpecificForceAlignmentPlusI => new PidController(),
            StabilizerChoice.PrivilegedFeedforward => new PrivilegedFullStateController(),
            _ => new HoldController(),
        };

        /// <summary>
        /// ④ replays the offline optimum stored next to the drive it was computed
        /// for (<c>&lt;drive&gt;.traj.csv</c>, written by the demo/ceiling runners).
        /// Returns null — and says why — when there is no trajectory for this
        /// drive, because replaying someone else's optimum would be meaningless.
        /// </summary>
        public static IController MakeHindsightCeiling(DriverTrace trace)
        {
            string path = TrajectoryPathFor(trace);
            if (path == null)
            {
                Debug.LogWarning("[④] no trajectory for this drive — the hindsight ceiling only exists " +
                                 "for a drive it was optimised on. Falling back to Rigid (Hold).");
                return null;
            }
            using (var r = new System.IO.StreamReader(path))
            {
                var rows = BcDatasetExporter.LoadTrajectory(r)
                    .ConvertAll(x => (x.T, x.PitchRate, x.RollRate));
                Debug.Log($"[④] hindsight ceiling ← {path} ({rows.Count} rows)");
                return new TrajectoryPlaybackController(rows);
            }
        }

        /// <summary>&lt;drive&gt;.trace.csv → &lt;drive&gt;.traj.csv, or null when absent.</summary>
        public static string TrajectoryPathFor(DriverTrace trace)
        {
            if (string.IsNullOrEmpty(trace?.SourcePath)) return null;
            string path = trace.SourcePath;
            const string ext = ".trace.csv";
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - ext.Length);
            path += ".traj.csv";
            return System.IO.File.Exists(path) ? path : null;
        }

        public static ISloshModel MakeSlosh(SloshModelChoice c) => c switch
        {
            SloshModelChoice.ShallowWater => new ShallowWaterSloshModel(),
            _ => new PendulumSloshModel(),
        };

        /// <summary>Short label for a stabilizer choice, for the comparison HUD.</summary>
        public static string ShortName(StabilizerChoice c) => c switch
        {
            StabilizerChoice.Off => "Rigid (no stabilizer)",
            StabilizerChoice.SpecificForceAlignment => "Rule: specific-force align",
            StabilizerChoice.SpecificForceAlignmentPlusI => "Rule: PID",
            StabilizerChoice.PrivilegedFeedforward => "Privileged feed-forward",
            StabilizerChoice.LearnedPolicy => "Learned policy (③)",
            StabilizerChoice.HindsightCeiling => "Hindsight ceiling (④)",
            StabilizerChoice.LearnedPolicyB => "새 정책 (재학습)",
            _ => c.ToString(),
        };

        private void Update() => Advance(Time.deltaTime);

        /// <summary>
        /// Step the simulation forward by <paramref name="wallDt"/> seconds of
        /// wall time (scaled by <see cref="TimeScale"/>), in fixed physics steps.
        /// Public so it can be driven from a test.
        /// </summary>
        public void Advance(double wallDt)
        {
            if (Loop == null) return;

            double dtPhysics = Loop.Config.PhysicsStepSeconds;
            _accumulator += wallDt * Mathf.Max(0.02f, TimeScale);

            int steps = 0;
            while (_accumulator >= dtPhysics && steps < MaxStepsPerFrame)
            {
                Loop.StepPhysics();
                _accumulator -= dtPhysics;
                steps++;
            }

            var gt = Loop.LatestGroundTruth;
            _simTime = Loop.SimTime;
            _speedKmh = Loop.VehicleState.Speed * 3.6;
            _minFreeboard = gt.MinFreeboard;
            _spillLitres = gt.CumulativeOverflowVolume * 1000.0;
        }
    }
}
