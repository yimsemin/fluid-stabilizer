namespace FluidStabilizer.Contracts
{
    /// <summary>
    /// A slosh model that can report the roughly-constant rotational inertia the
    /// liquid reflects onto each gimbal axis. The simulation adds this to the
    /// actuator's axis inertia so the angular-acceleration part of the liquid
    /// reaction is handled as added inertia (stable, no algebraic loop) while the
    /// state-dependent oscillatory part is fed back as an external load torque.
    /// </summary>
    public interface IReflectedInertiaSource
    {
        double ReflectedInertiaPitch { get; }
        double ReflectedInertiaRoll { get; }
    }
}
