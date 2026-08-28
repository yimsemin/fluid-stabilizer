using FluidStabilizer.Contracts;

namespace FluidStabilizer.Rl
{
    /// <summary>
    /// Flattens <see cref="PrivilegedObservation"/> to a fixed, roughly unit-scaled
    /// <c>float[]</c> for the asymmetric critic (design-review §11). These are the
    /// *extra* signals only — the Python side concatenates them with the actor
    /// observation to form the critic input, so the critic sees "everything the
    /// actor sees, plus ground truth" and the actor stays deployment-honest.
    ///
    /// Layout (<see cref="Size"/> = 21):
    ///   [0..2]   true specific force, tank frame / 10
    ///   [3..6]   true joint pitch/roll angle (/0.5), pitch/roll rate (/3.5)
    ///   [7..8]   actuator thermal load pitch, roll  (already [0,1])
    ///   [9..12]  true slosh pitch angle/rate, roll angle/rate (/0.5, /3)
    ///   [13..15] slosh pitch ω, roll ω (/6), damping ratio
    ///   [16..18] true liquid CoM, tank frame / tank size ~ (/1, /1, /1)
    ///   [19]     true slosh kinetic energy / 100
    ///   [20]     true min freeboard / 0.4
    /// </summary>
    public static class PrivilegedEncoder
    {
        public const int Size = 21;

        public static float[] Encode(in PrivilegedObservation p)
        {
            var f = new float[Size];

            f[0] = (float)(p.SpecificForceTankFrame.X / 10.0);
            f[1] = (float)(p.SpecificForceTankFrame.Y / 10.0);
            f[2] = (float)(p.SpecificForceTankFrame.Z / 10.0);

            f[3] = (float)(p.PitchAngle / 0.5);
            f[4] = (float)(p.RollAngle / 0.5);
            f[5] = (float)(p.PitchRate / 3.5);
            f[6] = (float)(p.RollRate / 3.5);

            f[7] = (float)p.PitchThermalLoad;
            f[8] = (float)p.RollThermalLoad;

            f[9] = (float)(p.SloshPitchAngle / 0.5);
            f[10] = (float)(p.SloshPitchRate / 3.0);
            f[11] = (float)(p.SloshRollAngle / 0.5);
            f[12] = (float)(p.SloshRollRate / 3.0);

            f[13] = (float)(p.SloshPitchOmega / 6.0);
            f[14] = (float)(p.SloshRollOmega / 6.0);
            f[15] = (float)p.SloshDampingRatio;

            f[16] = (float)p.LiquidComTankFrame.X;
            f[17] = (float)p.LiquidComTankFrame.Y;
            f[18] = (float)p.LiquidComTankFrame.Z;

            f[19] = (float)(p.SloshKineticEnergy / 100.0);
            f[20] = (float)(p.MinFreeboard / 0.4);

            return f;
        }
    }
}
