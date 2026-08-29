using System;
using System.Collections.Generic;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim.Sensors;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>
    /// The fixed-step simulation core (design-review §20 item 2). Owns the clock
    /// and the 600/200/100 Hz cadence, and each physics step advances:
    /// driver → vehicle → gimbal actuator → tank kinematics → slosh → (on sensor
    /// ticks) sensor sampling → (on control ticks) the controller. Fully
    /// deterministic given a seed; no Unity types.
    ///
    /// Drive it either with <see cref="RunEpisode"/> (headless, one call) or by
    /// <see cref="BeginEpisode"/> + repeated <see cref="StepPhysics"/> (for the
    /// Unity view and the RL adapter).
    /// </summary>
    public sealed class SimulationLoop
    {
        private readonly SimulationConfig _cfg;
        private IController _controller;
        private readonly IDriverInput _driver;
        private readonly ISloshModel _slosh;

        private readonly ManualClock _clock;
        private readonly VehicleModel _vehicle;
        private readonly RoadModel _road;
        private readonly GimbalActuator _gimbal;
        private readonly SensorSuite _sensors;

        private double _distanceTraveled;
        private RoadInput _lastRoad;

        private readonly int _latencySensorSteps;
        private readonly Queue<SensorBundle> _bundleQueue = new Queue<SensorBundle>();

        private StabilizerCommand _latchedCommand;
        private SloshOutput _lastSlosh;
        private double _lastSloshLoadPitch, _lastSloshLoadRoll;
        private long _commandCount;
        private double _effortAccum;
        private long _torqueSatTicks, _angleLimitTicks;

        private double _cumulativeOverflow;
        private double _worstFreeboard;
        private double _sloshEnergyIntegral;

        public SimulationLoop(SimulationConfig cfg, IController controller, IDriverInput driver,
            ISloshModel slosh = null)
        {
            cfg.Validate();
            _cfg = cfg;
            _controller = controller;
            _driver = driver;
            _slosh = slosh ?? new NullSloshModel();

            _clock = new ManualClock(cfg.PhysicsStepSeconds);
            _vehicle = new VehicleModel(cfg.Vehicle);
            _road = new RoadModel(cfg.Road);
            _gimbal = new GimbalActuator(cfg.Gimbal, cfg.PhysicsStepSeconds);
            _sensors = new SensorSuite(cfg.Sensors, cfg.Slosh);

            _latencySensorSteps = Math.Max(0,
                (int)Math.Round(cfg.Sensors.BundleLatencySeconds / (cfg.PhysicsStepSeconds * cfg.SensorDecimation)));
        }

        public IClock Clock => _clock;
        public SimulationConfig Config => _cfg;
        public IController Controller => _controller;

        /// <summary>The slosh model this loop is integrating (read-only; for the view).</summary>
        public ISloshModel SloshModel => _slosh;
        public long PhysicsStep => _clock.StepCount;
        public double SimTime => _clock.SimTimeSeconds;
        public bool EpisodeComplete =>
            _cfg.EpisodeSeconds > 0.0 && _clock.StepCount >= _cfg.TotalPhysicsSteps;

        /// <summary>
        /// Swap the stabilizer controller mid-run (a dev / interactive
        /// convenience — the new controller is <see cref="IController.Reset"/>).
        /// </summary>
        public void SetController(IController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _controller.Reset();
            _latchedCommand = StabilizerCommand.Hold(_commandCount, _clock.SimTimeSeconds);
        }

        public VehicleState VehicleState { get; private set; }
        public GimbalState GimbalState => _gimbal.State;
        public TankKinematicState TankState { get; private set; }
        public GroundTruthFrame LatestGroundTruth { get; private set; }
        public SensorBundle LatestSensorBundle { get; private set; }
        public StabilizerCommand LatestCommand => _latchedCommand;

        public ulong Seed { get; private set; }

        public void BeginEpisode(ulong seed)
        {
            Seed = seed;
            var root = new DeterministicRandom(seed);

            _clock.Reset();
            _vehicle.Reset();
            _road.Reset(root.Fork("road"));
            _gimbal.Reset();
            _sensors.Reset(root.Fork("sensors"));
            _driver.Reset();
            _controller.Reset();
            _slosh.Reset(_cfg.Slosh.ToParameters(), root.Fork("slosh"));

            if (_slosh is IReflectedInertiaSource ri)
                _gimbal.SetReflectedInertia(ri.ReflectedInertiaPitch, ri.ReflectedInertiaRoll);

            _bundleQueue.Clear();
            _latchedCommand = StabilizerCommand.Hold(0, 0.0);
            _lastSlosh = default;
            _lastSloshLoadPitch = _lastSloshLoadRoll = 0.0;
            _commandCount = 0;
            _effortAccum = 0;
            _torqueSatTicks = _angleLimitTicks = 0;
            _cumulativeOverflow = 0;
            _worstFreeboard = double.PositiveInfinity;
            _sloshEnergyIntegral = 0;
            _distanceTraveled = 0.0;
            _lastRoad = _road.Sample(0.0);

            // Prime derived state at t = 0 without advancing time.
            VehicleState = _vehicle.Step(DriverControls.Neutral, _lastRoad, 0.0);
            TankState = TankKinematics.Compute(VehicleState, _gimbal.State, _cfg.Gimbal.PivotInVehicle);
            LatestSensorBundle = _sensors.Sample(0.0, VehicleState, _gimbal.State, TankState, _lastSlosh);
            LatestGroundTruth = BuildGroundTruth();
        }

        /// <summary>Advance one physics step. Returns false once the episode is complete.</summary>
        public bool StepPhysics()
        {
            if (EpisodeComplete) return false;

            double dt = _cfg.PhysicsStepSeconds;
            double tNext = (_clock.StepCount + 1) * dt;

            DriverControls controls = _driver.Sample(tNext);

            RoadInput road = _road.Sample(_distanceTraveled);
            _lastRoad = road;
            VehicleState v = _vehicle.Step(controls, road, dt);
            _distanceTraveled += Math.Abs(v.Speed) * dt;

            // The gimbal sees the previous step's slosh reaction torque as an
            // external load (explicit one-step-lag coupling — stable at 600 Hz
            // for the liquid/cradle inertia ratio here; see docs/decisions.md D1).
            _gimbal.Step(_latchedCommand, _lastSloshLoadPitch, _lastSloshLoadRoll, dt);
            GimbalState g = _gimbal.State;

            TankKinematicState tank = TankKinematics.Compute(v, g, _cfg.Gimbal.PivotInVehicle);

            var sloshInput = new SloshInput
            {
                GravityTankFrame = tank.GravityTankFrame,
                LinearAccelerationTankFrame = tank.LinearAccelerationTankFrame,
                AngularVelocityTankFrame = tank.AngularVelocityTankFrame,
                AngularAccelerationTankFrame = tank.AngularAccelerationTankFrame,
                Dt = dt
            };
            SloshOutput slosh = _slosh.Step(sloshInput);
            _lastSlosh = slosh;
            ProjectSloshLoadOntoJoints(slosh.ReactionTorqueTankFrame, g);

            _cumulativeOverflow += Math.Max(0.0, slosh.OverflowVolumeThisStep);
            _worstFreeboard = Math.Min(_worstFreeboard, slosh.MinFreeboard);
            _sloshEnergyIntegral += _slosh.SampleGroundTruth().SloshKineticEnergy * dt;

            _clock.Advance();
            VehicleState = v;
            TankState = tank;

            long step = _clock.StepCount;

            if (step % _cfg.SensorDecimation == 0)
            {
                SensorBundle bundle = _sensors.Sample(_clock.SimTimeSeconds, v, g, tank, slosh);
                _bundleQueue.Enqueue(bundle);
                while (_bundleQueue.Count > _latencySensorSteps + 1) _bundleQueue.Dequeue();
                LatestSensorBundle = bundle;
            }

            if (step % _cfg.ControlDecimation == 0)
            {
                SensorBundle delivered = _bundleQueue.Count > 0 ? _bundleQueue.Peek() : LatestSensorBundle;
                _latchedCommand = _controller is IPrivilegedController pc
                    ? pc.ComputeCommand(delivered, BuildPrivilegedObservation())
                    : _controller.ComputeCommand(delivered);
                _commandCount++;
                // Effort = actual joint angular rate (mode-agnostic; measures
                // how hard the actuator is working).
                _effortAccum += 0.5 * (Math.Abs(g.Pitch.Rate) + Math.Abs(g.Roll.Rate));
                if (g.Pitch.TorqueSaturated || g.Roll.TorqueSaturated) _torqueSatTicks++;
                if (g.Pitch.AtAngleLimit || g.Roll.AtAngleLimit) _angleLimitTicks++;
            }

            LatestGroundTruth = BuildGroundTruth();
            return true;
        }

        public EpisodeResult RunEpisode(ulong seed)
        {
            BeginEpisode(seed);
            while (StepPhysics()) { }
            return Result();
        }

        public EpisodeResult Result() => new EpisodeResult
        {
            Seed = Seed,
            PhysicsSteps = _clock.StepCount,
            DurationSeconds = _clock.SimTimeSeconds,
            ControllerName = _controller.Name,
            DriverName = _driver.Name,
            SloshModelName = _slosh.Name,
            TotalOverflowVolume = _cumulativeOverflow,
            WorstFreeboard = double.IsPositiveInfinity(_worstFreeboard) ? 0.0 : _worstFreeboard,
            SloshEnergyIntegral = _sloshEnergyIntegral,
            MeanAbsActionRate = _commandCount > 0 ? _effortAccum / _commandCount : 0.0,
            TorqueSaturationFraction = _commandCount > 0 ? (double)_torqueSatTicks / _commandCount : 0.0,
            AngleLimitFraction = _commandCount > 0 ? (double)_angleLimitTicks / _commandCount : 0.0
        };

        /// <summary>
        /// Resolve the slosh reaction torque (tank frame) onto the two gimbal
        /// joint axes so the actuator feels a load that varies with the liquid
        /// motion. Stored for the next step's <c>_gimbal.Step</c>.
        /// </summary>
        private void ProjectSloshLoadOntoJoints(Double3 reactionTorqueTankFrame, in GimbalState g)
        {
            Double3x3 rVT = Double3x3.Gimbal(g.Pitch.Angle, g.Roll.Angle);
            Double3 tauV = rVT * reactionTorqueTankFrame;

            double cp = Math.Cos(g.Pitch.Angle), sp = Math.Sin(g.Pitch.Angle);
            var rollAxisV = new Double3(cp, 0.0, -sp);

            _lastSloshLoadPitch = tauV.Y;                       // pitch joint axis = V.Y
            _lastSloshLoadRoll = Double3.Dot(tauV, rollAxisV);  // roll joint axis
        }

        /// <summary>Assemble the privileged observation for the current step (critic / ceiling baseline only).</summary>
        public PrivilegedObservation BuildPrivilegedObservation()
        {
            GimbalState g = _gimbal.State;
            SloshGroundTruth sg = _slosh.SampleGroundTruth();
            double[] mode = sg.ModeState;

            return new PrivilegedObservation
            {
                StampSeconds = _clock.SimTimeSeconds,
                SpecificForceTankFrame = TankState.LinearAccelerationTankFrame - TankState.GravityTankFrame,
                PitchAngle = g.Pitch.Angle,
                RollAngle = g.Roll.Angle,
                PitchRate = g.Pitch.Rate,
                RollRate = g.Roll.Rate,
                PitchThermalLoad = g.Pitch.ThermalLoad,
                RollThermalLoad = g.Roll.ThermalLoad,
                SloshPitchAngle = mode.Length > 0 ? mode[0] : 0.0,
                SloshPitchRate = mode.Length > 1 ? mode[1] : 0.0,
                SloshRollAngle = mode.Length > 2 ? mode[2] : 0.0,
                SloshRollRate = mode.Length > 3 ? mode[3] : 0.0,
                SloshPitchOmega = sg.PitchModeOmega,
                SloshRollOmega = sg.RollModeOmega,
                SloshDampingRatio = sg.ModeDampingRatio,
                LiquidComTankFrame = sg.LiquidCenterOfMass,
                SloshKineticEnergy = sg.SloshKineticEnergy,
                MinFreeboard = double.IsPositiveInfinity(_worstFreeboard) ? 0.0 : _lastSlosh.MinFreeboard
            };
        }

        private GroundTruthFrame BuildGroundTruth()
        {
            GimbalState g = _gimbal.State;
            SloshGroundTruth sg = _slosh.SampleGroundTruth();
            Double3 tankSpecific = TankState.LinearAccelerationTankFrame - TankState.GravityTankFrame;

            return new GroundTruthFrame
            {
                StampSeconds = _clock.SimTimeSeconds,
                PhysicsStep = _clock.StepCount,
                VehiclePositionWorld = VehicleState.OriginWorld,
                VehicleSpeed = VehicleState.Speed,
                VehicleLongAccel = VehicleState.LongitudinalAcceleration,
                VehicleLatAccel = VehicleState.LateralAcceleration,
                VehicleAngularVelocityBody = VehicleState.AngularVelocityBody,
                PitchAngle = g.Pitch.Angle,
                RollAngle = g.Roll.Angle,
                PitchRate = g.Pitch.Rate,
                RollRate = g.Roll.Rate,
                PitchTorque = g.Pitch.AppliedTorque,
                RollTorque = g.Roll.AppliedTorque,
                AnyTorqueSaturated = g.Pitch.TorqueSaturated || g.Roll.TorqueSaturated,
                AnyAngleLimited = g.Pitch.AtAngleLimit || g.Roll.AtAngleLimit,
                TankSpecificForceTankFrame = tankSpecific,
                LiquidCenterOfMassTankFrame = sg.LiquidCenterOfMass,
                SloshKineticEnergy = sg.SloshKineticEnergy,
                MinFreeboard = double.IsPositiveInfinity(_worstFreeboard) ? 0.0 : _worstFreeboard,
                RoadGrade = _lastRoad.Grade,
                RoadBank = _lastRoad.Bank,
                RoadSurfaceHeight = _lastRoad.SurfaceHeight,
                CumulativeOverflowVolume = _cumulativeOverflow
            };
        }
    }
}
