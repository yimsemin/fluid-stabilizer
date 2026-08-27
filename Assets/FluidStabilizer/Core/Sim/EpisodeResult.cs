using System;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>Aggregate metrics for one finished episode (design-review §10, §11).</summary>
    [Serializable]
    public struct EpisodeResult
    {
        public ulong Seed;
        public long PhysicsSteps;
        public double DurationSeconds;
        public string ControllerName;
        public string DriverName;
        public string SloshModelName;

        /// <summary>Total liquid volume spilled over the rim (m³). The primary score.</summary>
        public double TotalOverflowVolume;

        /// <summary>Smallest freeboard reached at any point (m); negative means it overflowed.</summary>
        public double WorstFreeboard;

        /// <summary>Time-integrated slosh kinetic energy (J·s) — how much the liquid was left moving.</summary>
        public double SloshEnergyIntegral;

        /// <summary>Mean absolute commanded joint rate (rad/s) — control effort proxy.</summary>
        public double MeanAbsActionRate;

        /// <summary>Fraction of control ticks with actuator torque saturated.</summary>
        public double TorqueSaturationFraction;

        /// <summary>Fraction of control ticks with a joint at its travel limit.</summary>
        public double AngleLimitFraction;

        public override string ToString()
            => $"[{ControllerName} / {DriverName} / {SloshModelName}] seed={Seed} " +
               $"spill={TotalOverflowVolume:E3} m^3, worstFreeboard={WorstFreeboard:F4} m, " +
               $"sloshEnergy∫={SloshEnergyIntegral:F2}, effort={MeanAbsActionRate:F3}, " +
               $"satFrac={TorqueSaturationFraction:P1}, limFrac={AngleLimitFraction:P1}";
    }
}
