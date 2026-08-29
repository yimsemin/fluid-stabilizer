using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    public enum StabilizerChoice
    {
        Off,                    // tank held rigid to the bed (HoldController)
        SpecificForceAlignment,
        SpecificForceAlignmentPlusI,
        PrivilegedFeedforward,  // cheats (true slosh-free tilt) — for comparison only
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

        [Header("Runtime (read-only)")]
        [SerializeField] private double _simTime;
        [SerializeField] private double _speedKmh;
        [SerializeField] private double _minFreeboard;
        [SerializeField] private double _spillLitres;

        /// <summary>Tooling/test hook: drive with this instead of keyboard/gamepad. Set before BuildLoop.</summary>
        public IDriverInput DriverOverride;

        public SimulationLoop Loop { get; private set; }
        public HumanDriverInput Driver { get; private set; }
        public StabilizerChoice ActiveStabilizer { get; private set; }
        public SloshModelChoice ActiveSloshModel { get; private set; }

        private double _accumulator;
        private const int MaxStepsPerFrame = 40;

        private void Awake() => BuildLoop();

        public void BuildLoop()
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 0.0 }; // endless
            cfg.Slosh.FillRatio = FillRatio;
            cfg.Road.Enabled = Road;
            cfg.Road.Severity = RoadSeverity;

            Driver = new HumanDriverInput();
            Loop = new SimulationLoop(cfg, MakeController(Stabilizer),
                DriverOverride ?? Driver, MakeSlosh(SloshModel));
            Loop.BeginEpisode(Seed);
            ActiveStabilizer = Stabilizer;
            ActiveSloshModel = SloshModel;
            _accumulator = 0;
        }

        public void ResetEpisode()
        {
            Loop.SetController(MakeController(ActiveStabilizer));
            Loop.BeginEpisode(Seed);
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
            Loop.SetController(MakeController(choice));
        }

        public static IController MakeController(StabilizerChoice c) => c switch
        {
            StabilizerChoice.SpecificForceAlignment => new SpecificForceAlignmentController(),
            StabilizerChoice.SpecificForceAlignmentPlusI => new PidController(),
            StabilizerChoice.PrivilegedFeedforward => new PrivilegedFullStateController(),
            _ => new HoldController(),
        };

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
