namespace FluidStabilizer.Contracts
{
    /// <summary>
    /// True state a privileged controller or the training critic is allowed to
    /// see — never routed to a normal <see cref="IController"/> actor. All in the
    /// tank frame / SI. This is also the shape the RL critic will consume.
    /// </summary>
    public struct PrivilegedObservation
    {
        public double StampSeconds;

        /// <summary>True specific force (a − g) at the gimbal pivot, tank frame.</summary>
        public Double3 SpecificForceTankFrame;

        /// <summary>True gimbal joint state (no sensor noise/quantisation).</summary>
        public double PitchAngle;
        public double RollAngle;
        public double PitchRate;
        public double RollRate;

        /// <summary>Actuator winding heat per axis [0,1]; peak torque derates toward continuous as it rises.</summary>
        public double PitchThermalLoad;
        public double RollThermalLoad;

        /// <summary>True first-mode slosh pendulum state, per axis.</summary>
        public double SloshPitchAngle;
        public double SloshPitchRate;
        public double SloshRollAngle;
        public double SloshRollRate;

        /// <summary>True first-mode natural frequency (rad/s) and damping ratio.</summary>
        public double SloshPitchOmega;
        public double SloshRollOmega;
        public double SloshDampingRatio;

        /// <summary>True liquid centre of mass relative to the tank origin, tank frame.</summary>
        public Double3 LiquidComTankFrame;

        /// <summary>True slosh kinetic energy (J) and smallest current freeboard (m).</summary>
        public double SloshKineticEnergy;
        public double MinFreeboard;

        /// <summary>True vehicle body angular velocity / acceleration (body frame) — external disturbance for predictive controllers.</summary>
        public Double3 VehicleAngularVelocityBody;
        public Double3 VehicleAngularAccelerationBody;

        /// <summary>Liquid spilled so far this episode (m³); lowers the still-surface height.</summary>
        public double CumulativeOverflowVolume;
    }

    /// <summary>
    /// A controller that additionally receives <see cref="PrivilegedObservation"/>.
    /// Used only for the performance-ceiling baseline in evaluation — it must
    /// never be deployed. The plain <see cref="IController.ComputeCommand"/> is
    /// still implemented (it can ignore the sensors or fall back).
    /// </summary>
    public interface IPrivilegedController : IController
    {
        StabilizerCommand ComputeCommand(in SensorBundle sensors, in PrivilegedObservation privileged);
    }
}
