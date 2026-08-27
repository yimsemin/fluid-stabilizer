using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>
    /// Full true state at one control tick — for the training critic, evaluation
    /// metrics, and replay/debug logging ONLY. This must never reach a
    /// controller's <see cref="SensorBundle"/> (design-review: actor sees sensors,
    /// critic sees ground truth).
    /// </summary>
    [Serializable]
    public struct GroundTruthFrame
    {
        public double StampSeconds;
        public long PhysicsStep;

        // Vehicle
        public Double3 VehiclePositionWorld;
        public double VehicleSpeed;
        public double VehicleLongAccel;
        public double VehicleLatAccel;
        public Double3 VehicleAngularVelocityBody;

        // Gimbal
        public double PitchAngle;
        public double RollAngle;
        public double PitchRate;
        public double RollRate;
        public double PitchTorque;
        public double RollTorque;
        public bool AnyTorqueSaturated;
        public bool AnyAngleLimited;

        // Tank / slosh
        public Double3 TankSpecificForceTankFrame;   // true (a - g) at the pivot, tank frame
        public Double3 LiquidCenterOfMassTankFrame;
        public double SloshKineticEnergy;
        public double MinFreeboard;

        // Accumulated over the episode
        public double CumulativeOverflowVolume;
    }
}
