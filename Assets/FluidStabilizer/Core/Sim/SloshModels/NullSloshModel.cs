using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim.SloshModels
{
    /// <summary>
    /// Placeholder slosh model: the liquid is treated as a rigid mass fixed at
    /// the still-surface centroid — no wave dynamics, no reaction, never spills.
    /// Used to bring the simulation loop up end-to-end before the real
    /// equivalent-mechanical (pendulum) model exists. Not a valid model for
    /// training (design-review §6, option A).
    /// </summary>
    public sealed class NullSloshModel : ISloshModel
    {
        private double _restSurfaceZ;

        public string Name => "Null (rigid liquid, no dynamics)";

        public void Reset(SloshParameters p, IRandomSource rng)
        {
            _restSurfaceZ = p.FillRatio * p.TankHeightZ;
        }

        public SloshOutput Step(in SloshInput input) => new SloshOutput
        {
            ReactionForceTankFrame = Double3.Zero,
            ReactionTorqueTankFrame = Double3.Zero,
            OverflowVolumeThisStep = 0.0,
            MinFreeboard = _restSurfaceZ // constant; whole freeboard always "available"
        };

        public SloshGroundTruth SampleGroundTruth() => new SloshGroundTruth
        {
            LiquidCenterOfMass = new Double3(0, 0, 0.5 * _restSurfaceZ),
            SloshKineticEnergy = 0.0,
            ModeState = System.Array.Empty<double>()
        };
    }
}
