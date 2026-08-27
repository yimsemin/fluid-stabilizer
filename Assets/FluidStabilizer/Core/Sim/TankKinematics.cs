using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>Tank-frame kinematic quantities derived from vehicle + gimbal state.</summary>
    public struct TankKinematicState
    {
        public Double3x3 OrientationWorldFromTank;   // R_WT
        public Double3 GravityTankFrame;             // g expressed in tank frame (m/s²)
        public Double3 LinearAccelerationTankFrame;  // accel of the pivot, tank frame
        public Double3 AngularVelocityTankFrame;
        public Double3 AngularAccelerationTankFrame;
    }

    /// <summary>
    /// Computes the tank frame's motion from the vehicle body state and the gimbal
    /// joint state. The tank is kinematically attached to the vehicle through the
    /// gimbal (decision D1: no free tank rigid body in v1), so its motion is fully
    /// determined by these two.
    ///
    /// Small-angle transport cross-terms in the angular acceleration are dropped
    /// in v1 (documented approximation); the dominant terms — vehicle rotation,
    /// joint rotation, and the lever-arm linear transport — are kept exactly.
    /// </summary>
    public static class TankKinematics
    {
        public static TankKinematicState Compute(in VehicleState v, in GimbalState g, Double3 pivotInVehicle)
        {
            double pitch = g.Pitch.Angle;
            double roll = g.Roll.Angle;
            double pitchRate = g.Pitch.Rate;
            double rollRate = g.Roll.Rate;
            double pitchAcc = g.Pitch.AngularAccel;
            double rollAcc = g.Roll.AngularAccel;

            Double3x3 rVT = Double3x3.Gimbal(pitch, roll);       // tank -> vehicle
            Double3x3 rTV = rVT.Transpose();                     // vehicle -> tank
            Double3x3 rWT = v.OrientationWorldFromBody * rVT;    // tank -> world

            // Joint angular velocity/acceleration expressed in the vehicle frame.
            // Gimbal = Ry(pitch) then Rx(roll): pitch axis is V.Y, roll axis is Ry(pitch)*V.X.
            double cp = Math.Cos(pitch), sp = Math.Sin(pitch);
            var rollAxisV = new Double3(cp, 0.0, -sp);
            Double3 omegaJointV = new Double3(0, pitchRate, 0) + rollAxisV * rollRate;
            Double3 alphaJointV = new Double3(0, pitchAcc, 0) + rollAxisV * rollAcc;

            Double3 omegaV = v.AngularVelocityBody + omegaJointV;
            Double3 alphaV = v.AngularAccelerationBody + alphaJointV;

            // Linear acceleration of the pivot in the vehicle frame:
            //   a_pivot = a_origin + alpha x r + omega x (omega x r)
            Double3 r = pivotInVehicle;
            Double3 aPivotV = v.LinearAccelerationBody
                              + Double3.Cross(alphaV, r)
                              + Double3.Cross(omegaV, Double3.Cross(omegaV, r));

            Double3 gravityWorld = new Double3(0, 0, -MathD.StandardGravity);

            return new TankKinematicState
            {
                OrientationWorldFromTank = rWT,
                GravityTankFrame = rWT.InverseTimes(gravityWorld),
                LinearAccelerationTankFrame = rTV * aPivotV,
                AngularVelocityTankFrame = rTV * omegaV,
                AngularAccelerationTankFrame = rTV * alphaV
            };
        }
    }
}
