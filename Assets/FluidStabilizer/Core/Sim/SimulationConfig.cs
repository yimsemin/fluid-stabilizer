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

        /// <summary>Episode length (s). Zero or negative = run indefinitely (interactive play).</summary>
        public double EpisodeSeconds = 30.0;

        public VehicleConfig Vehicle = new VehicleConfig();
        public GimbalConfig Gimbal = new GimbalConfig();
        public SloshConfig Slosh = new SloshConfig();
        public SensorConfig Sensors = new SensorConfig();
        public RoadConfig Road = new RoadConfig();

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

        // Suspension heave (vertical) response to the road surface — a base-excited
        // spring-mass-damper (design-review §4: the suspension is a low-pass with
        // a ~1–2 Hz resonance that shapes the disturbance the tank feels). Only
        // active when a RoadModel feeds a non-zero surface height.
        public double HeaveNaturalFreqHz = 1.6;
        public double HeaveDampingRatio = 0.30;

        /// <summary>Static pitch gain: body pitch (rad) per 1 m/s² of longitudinal accel.</summary>
        public double PitchPerLongAccel = 0.010;

        /// <summary>Static roll gain: body roll (rad) per 1 m/s² of lateral accel.</summary>
        public double RollPerLatAccel = 0.012;

        /// <summary>Wheel track width (m, left wheel to right wheel). Sets the roll
        /// angle a left/right road-height difference produces.</summary>
        public double TrackWidthM = 2.0;

        /// <summary>How much of the geometric road-roll (from the L/R wheel-height
        /// difference) reaches the body, before the roll suspension filters it.</summary>
        public double RoadRollGain = 1.0;

        // --- lumped tyre-slip proxy (slippery-road feel; design-review defers a
        //     full tyre model, this is the cheap stand-in). Engages only when the
        //     demanded acceleration approaches the friction limit. ---
        /// <summary>Demand / friction-limit ratio at which the tyres start to break
        /// loose (slightly over 1 — at the limit you still grip; beyond it you slide).
        /// So normal hard driving stays clean and only genuinely over-the-limit
        /// inputs, or a low-grip road that shrinks the limit, cause a slide.</summary>
        public double SlipOnsetFraction = 1.02;
        /// <summary>Demand-ratio span above onset over which slip ramps 0 → 1.</summary>
        public double SlipSaturationSpan = 0.5;
        /// <summary>Extra grip lost once fully sliding (kinetic &lt; static friction).</summary>
        public double SlipGripLoss = 0.28;
        /// <summary>Yaw-rate wander amplitude while sliding (rad/s per unit slip).</summary>
        public double SlipYawWander = 0.35;
        /// <summary>Time constant of the yaw-wander noise (s).</summary>
        public double SlipYawTau = 0.5;
        /// <summary>Steering under-response lag while fully sliding (s) — collapses as grip returns (the "grab").</summary>
        public double SlipYawLag = 0.28;
        /// <summary>Slip onset / recovery time constants (s) — quick to break loose, slower to regain grip.</summary>
        public double SlipOnsetTau = 0.08;
        public double SlipRecoverTau = 0.35;

        /// <summary>Initial speed (m/s).</summary>
        public double InitialSpeed = 12.0;

        // --- lane-keeping (only active when the road is on) ---
        // The driver has full steering authority. The road's curvature is
        // followed automatically (so a hands-off truck doesn't leave the road on
        // a bend), but there is only a *weak* pull back toward the road centre —
        // enough that the driver can change lanes and hold a chosen lane for a
        // while. The road edge is a firm wall.
        /// <summary>Weak return-to-centre yaw gain (1/s per metre of offset, ÷ speed).</summary>
        public double LaneReturnGain = 0.12;
        /// <summary>Firm restoring yaw gain once past the road edge (per metre over).</summary>
        public double RoadEdgeGain = 0.9;
    }

    [Serializable]
    public sealed class GimbalConfig
    {
        /// <summary>Position of the gimbal pivot in the Vehicle frame (m).</summary>
        public Double3 PivotInVehicle = new Double3(2.6, 0.0, 1.4);

        public double MaxAngleRad = 30.0 * MathD.Deg2Rad;
        public double MaxRateRadPerSec = 3.5;      // per axis

        /// <summary>
        /// Peak (short-term) output torque per axis. 900 N·m covers the
        /// quasi-static slosh moment at the 0.45 g friction-circle limit
        /// (≈ 740 N·m) with modest margin, but leaves the actuator clearly
        /// under-powered for aggressive dynamic tilt tracking — the deliberate
        /// authority-limited challenge (decision D5).
        /// </summary>
        public double MaxTorqueNm = 900.0;

        /// <summary>
        /// Continuous (thermally sustainable) torque per axis. Above this the
        /// winding heats and the available peak derates toward this value
        /// (I²t model in <see cref="GimbalActuator"/>); bursts are fine,
        /// sustained hard maneuvers are not.
        /// </summary>
        public double ContinuousTorqueNm = 420.0;

        /// <summary>Winding thermal time constant (s) for the peak-torque derate.</summary>
        public double ThermalTimeConstantSeconds = 15.0;

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

        // --- lock-capable hybrid action (decision D8, 묶음 3; see LockGate) ---
        // Only consulted on a control path that builds a LockGate from this config
        // (residual RL with -rlLockCapable, the hindsight actor with opts.LockCapable).
        // Default behaviour is unchanged.

        /// <summary>Composed active rate command (rad/s) below which — with the joint
        /// near zero — the actor mechanically locks instead of running the servo.</summary>
        public double LockRateThresholdRadPerSec = 0.06;

        /// <summary>Only lock when both joints are within this of zero (rad); keeps the
        /// actuator's instantaneous snap-to-zero on lock negligible.</summary>
        public double LockAngleThresholdRad = 0.5 * MathD.Deg2Rad;

        /// <summary>Hysteresis: a locked joint releases only when the rate demand
        /// exceeds <see cref="LockRateThresholdRadPerSec"/> × this.</summary>
        public double UnlockRateFactor = 2.5;

        /// <summary>Minimum control ticks a lock persists before it may release — anti-chatter.</summary>
        public int MinLockDwellTicks = 8;
    }

    [Serializable]
    public sealed class RoadConfig
    {
        /// <summary>
        /// Master switch. False = perfectly flat, straight, level road (the v1
        /// default — keeps every existing scenario and determinism guard
        /// unchanged). True = <see cref="RoadModel"/> feeds grade, bank and
        /// surface roughness each step.
        /// </summary>
        public bool Enabled = false;

        // ---- hills: a slow, sustained grade ----
        /// <summary>Peak road grade, rad (~0.035 ≈ 3.5 %, a normal rolling highway; ~0.06 is steep).</summary>
        public double GradeAmplitudeRad = 0.035;
        public double HillWavelengthM = 140.0;

        // ---- horizontal curve: the road bends, and the truck follows it ----
        /// <summary>Peak centreline curvature, rad/m (~0.004 ≈ a 250 m-radius highway curve).</summary>
        public double CurvatureAmplitudeRadPerM = 0.004;
        public double CurvatureWavelengthM = 180.0;

        // ---- road geometry (for the lane model + rendering) ----
        /// <summary>Width of one lane, m.</summary>
        public double LaneWidthM = 3.5;
        /// <summary>Number of lanes (both directions). 4 → a ~14 m carriageway.</summary>
        public int LaneCount = 4;
        /// <summary>Half the drivable width (m). Past this the truck hits the firm edge.</summary>
        public double RoadHalfWidthM => 0.5 * LaneCount * LaneWidthM;

        // ---- curve superelevation / cross-slope ----
        /// <summary>Banking into the curve: superelevation (rad) per unit curvature (rad/m). Clamped to ±0.08.</summary>
        public double BankPerCurvature = 24.0;
        /// <summary>Independent cross-slope on top of superelevation.</summary>
        public double BankAmplitudeRad = 0.012;
        public double BankWavelengthM = 90.0;

        // ---- continuous surface roughness (asphalt texture + washboard) ----
        /// <summary>RMS vertical roughness of the road surface, m (~6 mm = decent asphalt; ~15 mm = coarse/broken).</summary>
        public double RoughnessRmsM = 0.006;
        public double RoughnessShortWavelengthM = 0.6;
        public double RoughnessLongWavelengthM = 9.0;

        /// <summary>
        /// Left/right wheel-track differential roughness, as a fraction of
        /// <see cref="RoughnessRmsM"/>. The two wheel tracks share the long
        /// undulations but decorrelate at short wavelength; this fraction sets how
        /// much independent per-wheel roughness there is → a road-induced roll
        /// disturbance. 0 = perfectly symmetric road (the old single-track model).
        /// </summary>
        public double WheelTrackRoughnessFrac = 0.35;

        // ---- discrete events: expansion joints, potholes, stray rocks, curbs ----
        public double EventsPerKm = 18.0;
        /// <summary>Mean |height| of a discrete event, m. Sign is random per event (bump vs pothole).</summary>
        public double EventHeightM = 0.05;
        public double EventLengthM = 0.4;

        /// <summary>
        /// Master multiplier on roughness + event size (not grade/bank) — the
        /// single knob for a curriculum or domain randomization: 0 = smooth,
        /// 1 = nominal, &gt;1 = rough.
        /// </summary>
        public double Severity = 1.0;
    }

    [Serializable]
    public sealed class SloshConfig
    {
        public double TankLengthX = 1.2;
        public double TankWidthY = 0.7;
        // Rim height above the floor (m). Lowered 0.8 → 0.6 on 2026-08-29: a
        // shallower open tank spills far more readily on a bump or a hard
        // maneuver, which is the regime the three-way comparison lives in. The
        // F1–F4 numeric tables in docs/findings.md predate this and need a
        // re-sweep; the *direction* of every finding is unchanged.
        public double TankHeightZ = 0.6;
        public double FillRatio = 0.5;      // 0.30 m liquid ⇒ 0.30 m freeboard (nominal)
        public double LiquidDensity = 1000.0;
        public double NaturalFrequencyScale = 1.0;

        /// <summary>Linear (small-amplitude) first-mode damping ratio.</summary>
        public double DampingRatio = 0.08;

        /// <summary>
        /// Amplitude-dependent damping: an extra torque ∝ θ̇·|θ̇| that models the
        /// energy lost when a large slosh wave breaks. Keeps the reduced-order
        /// model from running far outside its small-amplitude validity under hard
        /// maneuvers (Dodge/Keulegan — slosh damping grows with amplitude).
        /// </summary>
        public double QuadraticDamping = 0.9;
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
            QuadraticDamping = QuadraticDamping,
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
