using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim.SloshModels
{
    /// <summary>
    /// Placeholder model: the liquid is a single rigid mass fixed at its
    /// still-fill centroid — no wave dynamics, never spills (design-review §6,
    /// option A). It still returns the correct rigid-body reaction (weight +
    /// inertia) so the load cells and actuator load are sane. Useful for bringing
    /// the loop up and as an analytic reference; NOT a training model.
    /// </summary>
    public sealed class NullSloshModel : ISloshModel
    {
        private double _restSurfaceZ;
        private double _mass;
        private Double3 _centroid;

        public string Name => "Null (rigid liquid, no dynamics)";

        public void Reset(SloshParameters p, IRandomSource rng)
        {
            double h = p.FillRatio * p.TankHeightZ;
            _restSurfaceZ = h;
            _mass = p.LiquidDensity * p.TankLengthX * p.TankWidthY * h;
            _centroid = new Double3(0, 0, 0.5 * h);
        }

        public SloshOutput Step(in SloshInput input)
        {
            Double3 aCentroid = input.LinearAccelerationTankFrame
                                + Double3.Cross(input.AngularAccelerationTankFrame, _centroid)
                                + Double3.Cross(input.AngularVelocityTankFrame,
                                    Double3.Cross(input.AngularVelocityTankFrame, _centroid));
            Double3 force = -_mass * (aCentroid - input.GravityTankFrame);

            return new SloshOutput
            {
                ReactionForceTankFrame = force,
                ReactionTorqueTankFrame = Double3.Cross(_centroid, force),
                OverflowVolumeThisStep = 0.0,
                MinFreeboard = _restSurfaceZ
            };
        }

        public SloshGroundTruth SampleGroundTruth() => new SloshGroundTruth
        {
            LiquidCenterOfMass = _centroid,
            SloshKineticEnergy = 0.0,
            ModeState = System.Array.Empty<double>()
        };
    }
}
