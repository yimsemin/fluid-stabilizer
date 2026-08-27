namespace FluidStabilizer.Contracts
{
    /// <summary>Normalized driver controls for one simulation step.</summary>
    public struct DriverControls
    {
        /// <summary>Throttle demand, [0, 1].</summary>
        public double Throttle;

        /// <summary>Brake demand, [0, 1].</summary>
        public double Brake;

        /// <summary>Steering demand, [-1, 1] (left positive, matching the +Y=left frame).</summary>
        public double Steer;

        public static DriverControls Neutral => new DriverControls { Throttle = 0, Brake = 0, Steer = 0 };
    }

    /// <summary>
    /// Source of driver input. The human implementation reads a keyboard/gamepad
    /// in real time; scripted implementations generate stochastic driver profiles
    /// or replay recorded human traces for headless training. The stabilizer AI
    /// treats whatever this produces as an external disturbance.
    /// </summary>
    public interface IDriverInput
    {
        string Name { get; }

        void Reset();

        /// <summary>
        /// Controls for the step ending at <paramref name="simTimeSeconds"/>.
        /// Scripted profiles are deterministic given their seed and this time.
        /// </summary>
        DriverControls Sample(double simTimeSeconds);
    }
}
