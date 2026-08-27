using System;

namespace FluidStabilizer.Contracts
{
    /// <summary>
    /// Which stabilizer axis. Pitch rotates about the Vehicle Y axis, Roll about
    /// the once-pitched X axis (see docs/frames-units-conventions.md).
    /// </summary>
    public enum Axis
    {
        Pitch = 0,
        Roll = 1
    }

    /// <summary>
    /// Inertial measurement, mirrors <c>sensor_msgs/Imu</c> (the parts we use).
    /// All quantities are expressed in the sensor's own frame.
    /// </summary>
    [Serializable]
    public struct ImuSample
    {
        public double StampSeconds;

        /// <summary>
        /// Specific force (a − g) in m/s², sensor frame. This is what a real
        /// accelerometer reports: it cannot separate gravity from motion.
        /// </summary>
        public Double3 SpecificForce;

        /// <summary>Angular velocity in rad/s, sensor frame.</summary>
        public Double3 AngularVelocity;
    }

    /// <summary>Stabilizer joint state, mirrors <c>sensor_msgs/JointState</c>.</summary>
    [Serializable]
    public struct JointStateSample
    {
        public double StampSeconds;

        /// <summary>Measured joint angles (rad): index 0 = Pitch, 1 = Roll.</summary>
        public double PitchAngle;
        public double RollAngle;

        /// <summary>Measured joint angular rates (rad/s).</summary>
        public double PitchRate;
        public double RollRate;
    }

    /// <summary>Per-axis actuator electrical/mechanical estimate ("felt load").</summary>
    [Serializable]
    public struct MotorStateSample
    {
        public double StampSeconds;

        /// <summary>Estimated motor current (A).</summary>
        public double PitchCurrent;
        public double RollCurrent;

        /// <summary>Estimated output torque (N·m), derived from current.</summary>
        public double PitchTorque;
        public double RollTorque;
    }

    /// <summary>Vertical forces at fixed mount points under the tank (N).</summary>
    [Serializable]
    public struct LoadCellSample
    {
        public double StampSeconds;

        /// <summary>
        /// Reading per cell (N), same order as the configured mount-point layout.
        /// v1 uses 4 cells at the tank-cradle corners.
        /// </summary>
        public double[] Forces;
    }

    /// <summary>Optional point liquid-surface heights (m) at fixed probe locations.</summary>
    [Serializable]
    public struct LevelSample
    {
        public double StampSeconds;

        /// <summary>Surface height above the tank floor at each probe (m).</summary>
        public double[] Heights;
    }

    /// <summary>
    /// Optional signals a real truck exposes over CAN. Behind a config flag: it
    /// is a partial preview of the disturbance ("driver intent"), so training
    /// runs both with and without it (design-review §10).
    /// </summary>
    [Serializable]
    public struct VehicleCanSample
    {
        public double StampSeconds;
        public double SpeedMetersPerSecond;
        public double SteerAngleRadians;
        public double Throttle; // [0, 1]
        public double Brake;    // [0, 1]
    }

    /// <summary>
    /// One synchronized set of sensor readings for a single control tick. This is
    /// the entire input the controller/policy is allowed to see.
    ///
    /// Optional sensors use an explicit <c>Has*</c> flag rather than a nullable
    /// (nullables do not survive Unity serialization, and a wire/ROS mapping
    /// carries presence explicitly anyway). When a flag is false the matching
    /// field is undefined.
    /// </summary>
    [Serializable]
    public struct SensorBundle
    {
        public double StampSeconds;

        public ImuSample VehicleImu;
        public ImuSample TankImu;
        public JointStateSample Joints;
        public MotorStateSample Motors;
        public LoadCellSample LoadCells;

        public bool HasLevels;
        public LevelSample Levels;

        public bool HasVehicleCan;
        public VehicleCanSample VehicleCan;
    }
}
