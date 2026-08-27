using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>
    /// All tunable parameters for one simulation run. Defaults are the locked v1
    /// values from docs/frames-units-conventions.md and design-review.md. Domain
    /// randomization (a later step) samples fields of the nested configs per
    /// episode; the values here are the nominal centre.
    /// </summary>
    [Serializable]
    public sealed class SimulationConfig
    {
        // ---- timing (docs/frames-units-conventions.md) ----
        // Locked v1 cadence: physics 600 Hz, sensors 200 Hz, control 100 Hz.
        // Decimations are integers and control ticks land on sensor ticks.
        public double PhysicsRateHz = 600.0;
        public int SensorDecimation = 3;   // 600 / 3 = 200 Hz
        public int ControlDecimation = 6;  // 600 / 6 = 100 Hz

        public double EpisodeSeconds = 30.0;

        public VehicleConfig Vehicle = new VehicleConfig();
        public GimbalConfig Gimbal = new GimbalConfig();
        public SloshConfig Slosh = new SloshConfig();
        public SensorConfig Sensors = new SensorConfig();

        public double PhysicsStepSeconds => 1.0 / PhysicsRateHz;
        public long TotalPhysicsSteps => (long)Math.Round(EpisodeSeconds * PhysicsRateHz);

        public void Validate()
        {
            if (PhysicsRateHz <= 0) throw new ArgumentException("PhysicsRateHz must be > 0");
            if (SensorDecimation < 1) throw new ArgumentException("SensorDecimation must be >= 1");
            if (ControlDecimation < 1) throw new ArgumentException("ControlDecimation must be >= 1");
            // A control tick must coincide with a sensor tick so the controller
            // always has a fresh synchronized bundle.
            if (ControlDecimation % SensorDecimation != 0)
                throw new ArgumentException("ControlDecimation must be a multiple of SensorDecimation");
        }
    }

    [Serializable]
    public sealed class VehicleConfig
    {
        /// <summary>Total vehicle mass (kg). Large — slosh reaction does not move it in v1.</summary>
        public double Mass = 4000.0;

        /// <summary>Max forward acceleration from full throttle on level ground (m/s²).</summary>
        public double MaxAccel = 2.2;

        /// <summary>Max deceleration from full brake (m/s²). ~0.45 g — a hard brake for a laden truck.</summary>
        public double MaxBrake = 4.5;

        /// <summary>
        /// Tyre–road friction limit for the accel/steer friction circle (m/s²).
        /// ~0.45 g — a laden truck near its handling limit; sustained lateral
        /// beyond this is a rollover risk, not normal driving.
        /// </summary>
        public double FrictionLimit = 4.5;

        /// <summary>Wheelbase (m), rear-axle origin to front axle.</summary>
        public double Wheelbase = 4.0;

        /// <summary>Height of the sprung-mass CoG above the ground (m) — sets load transfer.</summary>
        public double CogHeight = 1.1;

        // Suspension as a 2nd-order response of body pitch/roll to load transfer.
        public double PitchNaturalFreqHz = 1.3;
        public double PitchDampingRatio = 0.35;
        public double RollNaturalFreqHz = 1.6;
        public double RollDampingRatio = 0.35;

        /// <summary>Static pitch gain: body pitch (rad) per 1 m/s² of longitudinal accel.</summary>
        public double PitchPerLongAccel = 0.010;

        /// <summary>Static roll gain: body roll (rad) per 1 m/s² of lateral accel.</summary>
        public double RollPerLatAccel = 0.012;

        /// <summary>Initial speed (m/s).</summary>
        public double InitialSpeed = 12.0;
    }

    [Serializable]
    public sealed class GimbalConfig
    {
        /// <summary>Position of the gimbal pivot in the Vehicle frame (m).</summary>
        public Double3 PivotInVehicle = new Double3(2.6, 0.0, 1.4);

        public double MaxAngleRad = 30.0 * MathD.Deg2Rad;
        public double MaxRateRadPerSec = 3.5;      // per axis
        public double MaxTorqueNm = 650.0;         // peak, per axis
        public double ContinuousTorqueNm = 280.0;  // thermal limit, per axis

        /// <summary>Closed-loop actuator bandwidth (Hz) and damping for the position/rate servo.</summary>
        public double BandwidthHz = 4.0;
        public double DampingRatio = 0.9;

        /// <summary>
        /// Servo integral gain (1/s²·rad → N·m via inertia). Removes steady-state
        /// angle sag under a constant external load, as a real PID gimbal does.
        /// </summary>
        public double IntegralGain = 40.0;

        /// <summary>Backlash dead-band half-width (rad). 0 disables backlash.</summary>
        public double BacklashRad = 0.0;

        /// <summary>Pure transport delay on received commands (s).</summary>
        public double CommandDelaySeconds = 0.015;

        /// <summary>Coulomb + viscous friction at the joint (N·m, N·m·s/rad).</summary>
        public double CoulombFrictionNm = 3.0;
        public double ViscousFrictionNmsPerRad = 1.5;

        /// <summary>Rotational inertia the actuator sees for the bare tank cradle (kg·m²), per axis.</summary>
        public double AxisInertia = 55.0;
    }

    [Serializable]
    public sealed class SloshConfig
    {
        public double TankLengthX = 1.2;
        public double TankWidthY = 0.7;
        public double TankHeightZ = 0.8;
        public double FillRatio = 0.5;      // 0.40 m liquid ⇒ 0.40 m freeboard (nominal)
        public double LiquidDensity = 1000.0;
        public double NaturalFrequencyScale = 1.0;
        public double DampingRatio = 0.10;
        public double ParticipatingMassScale = 1.0;
        public double SurfaceSlopePerPendulumAngle = 1.0;
        public double SpillDischargeCoefficient = 0.6;

        /// <summary>Empty tank + cradle mass (kg).</summary>
        public double DryMass = 40.0;

        public SloshParameters ToParameters() => new SloshParameters
        {
            TankLengthX = TankLengthX,
            TankWidthY = TankWidthY,
            TankHeightZ = TankHeightZ,
            FillRatio = FillRatio,
            LiquidDensity = LiquidDensity,
            NaturalFrequencyScale = NaturalFrequencyScale,
            DampingRatio = DampingRatio,
            ParticipatingMassScale = ParticipatingMassScale,
            SurfaceSlopePerPendulumAngle = SurfaceSlopePerPendulumAngle,
            SpillDischargeCoefficient = SpillDischargeCoefficient
        };
    }

    [Serializable]
    public sealed class SensorConfig
    {
        /// <summary>Master switch: when false all noise/bias/delay is zero (debug / analytic checks).</summary>
        public bool Enabled = true;

        public double ImuAccelNoiseDensity = 0.02;   // (m/s²)/√Hz-ish, applied per sample as sigma
        public double ImuGyroNoiseDensity = 0.002;   // (rad/s) per sample sigma
        public double ImuAccelBias = 0.05;           // fixed bias magnitude scale, sampled per episode
        public double ImuGyroBias = 0.003;

        public double EncoderQuantizationRad = 2.0 * Math.PI / 65536.0; // 16-bit
        public double EncoderNoiseRad = 1.0e-4;

        public double LoadCellNoiseN = 1.5;
        public double MotorCurrentNoiseA = 0.05;

        public bool EnableLevelSensors = false;
        public int LevelSensorCount = 3;
        public double LevelSensorNoiseM = 0.003;

        public bool EnableVehicleCan = false;

        /// <summary>Extra transport latency applied to the whole sensor bundle (s).</summary>
        public double BundleLatencySeconds = 0.005;
    }
}
