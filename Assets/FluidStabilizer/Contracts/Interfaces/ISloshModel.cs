namespace FluidStabilizer.Contracts
{
    /// <summary>
    /// Inputs to one slosh integration step, all in the Tank frame.
    /// </summary>
    public struct SloshInput
    {
        /// <summary>Gravity vector expressed in the tank frame (m/s²).</summary>
        public Double3 GravityTankFrame;

        /// <summary>Linear acceleration of the tank-frame origin, tank frame (m/s²).</summary>
        public Double3 LinearAccelerationTankFrame;

        /// <summary>Tank angular velocity, tank frame (rad/s).</summary>
        public Double3 AngularVelocityTankFrame;

        /// <summary>Tank angular acceleration, tank frame (rad/s²).</summary>
        public Double3 AngularAccelerationTankFrame;

        /// <summary>Integration step (s). Equal to the physics fixed step in v1.</summary>
        public double Dt;
    }

    /// <summary>Reaction the moving liquid applies back to the tank, plus spill state.</summary>
    public struct SloshOutput
    {
        /// <summary>Force on the tank body from the liquid, tank frame (N).</summary>
        public Double3 ReactionForceTankFrame;

        /// <summary>Torque on the tank body about the tank origin, tank frame (N·m).</summary>
        public Double3 ReactionTorqueTankFrame;

        /// <summary>Liquid volume that crossed the rim during this step (m³, ≥ 0).</summary>
        public double OverflowVolumeThisStep;

        /// <summary>
        /// Smallest freeboard remaining anywhere along the rim this step (m):
        /// distance from the estimated surface peak to the rim. Negative while
        /// overflowing. Used for the near-rim reward shaping term.
        /// </summary>
        public double MinFreeboard;
    }

    /// <summary>
    /// Privileged slosh state — for the training critic and for logging/eval only.
    /// NEVER routed into a controller's <see cref="SensorBundle"/>.
    /// </summary>
    public struct SloshGroundTruth
    {
        /// <summary>Liquid centre of mass relative to the tank origin, tank frame (m).</summary>
        public Double3 LiquidCenterOfMass;

        /// <summary>Total kinetic energy currently in the sloshing motion (J).</summary>
        public double SloshKineticEnergy;

        /// <summary>Current mode amplitudes (model-defined; e.g. pendulum angles, rad).</summary>
        public double[] ModeState;
    }

    /// <summary>
    /// Reduced-order model of liquid sloshing in the tank. v1 implementation is an
    /// equivalent mechanical model (one pendulum per axis) integrated in managed
    /// code at the physics rate and coupled both ways to the tank rigid body and
    /// the stabilizer joint. Shallow-water and multi-mode models are drop-in
    /// replacements behind this interface (design-review §6).
    /// </summary>
    public interface ISloshModel
    {
        string Name { get; }

        /// <summary>Configure for a new episode. <paramref name="parameters"/> is model-specific.</summary>
        void Reset(SloshParameters parameters, IRandomSource rng);

        /// <summary>Advance one step and return the reaction on the tank + spill state.</summary>
        SloshOutput Step(in SloshInput input);

        /// <summary>Privileged read-out. Training-critic / logging only.</summary>
        SloshGroundTruth SampleGroundTruth();
    }

    /// <summary>
    /// Physical parameters of a fill for one episode. Ranges are domain-randomized
    /// (design-review §13). Geometry-derived quantities (natural frequency,
    /// participating mass) may be computed by the model from these.
    /// </summary>
    public struct SloshParameters
    {
        public double TankLengthX;      // m, internal
        public double TankWidthY;       // m, internal
        public double TankHeightZ;      // m, internal (rim height above floor)
        public double FillRatio;        // [0,1] of tank height
        public double LiquidDensity;    // kg/m³

        /// <summary>Multiplier on the analytically-derived first-mode natural frequency.</summary>
        public double NaturalFrequencyScale;

        /// <summary>First-mode damping ratio (dimensionless).</summary>
        public double DampingRatio;

        /// <summary>
        /// Multiplier on the analytically-derived first-mode participating mass
        /// fraction (1.0 = use the textbook value).
        /// </summary>
        public double ParticipatingMassScale;

        /// <summary>
        /// Free-surface elevation at the wall per unit equivalent-pendulum angle,
        /// as a fraction of the half-length. ~1.0 nominal; needs calibration
        /// against a shallow-water run (design-review §6 caveat).
        /// </summary>
        public double SurfaceSlopePerPendulumAngle;

        /// <summary>Weir discharge coefficient for the continuous overflow estimate (~0.6).</summary>
        public double SpillDischargeCoefficient;
    }
}
