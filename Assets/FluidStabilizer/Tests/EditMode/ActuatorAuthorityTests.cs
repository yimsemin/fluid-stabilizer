using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// How much does the ceiling controller beat Hold as a function of actuator
    /// authority? Characterises whether the task is controllable with a realistic
    /// gimbal (design-review risk #1).
    /// </summary>
    [Explicit("diagnostic")]
    public class ActuatorAuthorityTests
    {
        private static (double hold, double priv) Compare(Action<GimbalConfig> tweak, double fill = 0.625)
        {
            double Run(Func<IController> make)
            {
                var cfg = new SimulationConfig { EpisodeSeconds = 30.0 };
                cfg.Slosh.FillRatio = fill;
                tweak(cfg.Gimbal);
                double total = 0;
                for (ulong seed = 1; seed <= 6; seed++)
                    total += new SimulationLoop(cfg, make(), new RandomManeuverDriver(seed, 0.6),
                        new PendulumSloshModel()).RunEpisode(seed).TotalOverflowVolume;
                return total / 6.0 * 1000.0; // litres
            }
            return (Run(() => new HoldController()), Run(() => new PrivilegedFullStateController()));
        }

        [Test]
        public void Sweep()
        {
            var cases = new (string, Action<GimbalConfig>)[]
            {
                ("nominal (±30°, 3.5 rad/s, 650 Nm, 4 Hz)", g => { }),
                ("travel ±45°",                              g => g.MaxAngleRad = 45 * MathD.Deg2Rad),
                ("travel ±60°",                              g => g.MaxAngleRad = 60 * MathD.Deg2Rad),
                ("2x rate (7 rad/s)",                        g => g.MaxRateRadPerSec = 7.0),
                ("2x torque (1300 Nm)",                      g => g.MaxTorqueNm = 1300),
                ("2x bandwidth (8 Hz)",                      g => g.BandwidthHz = 8.0),
                ("all: ±60°, 7 rad/s, 1300 Nm, 8 Hz", g =>
                {
                    g.MaxAngleRad = 60 * MathD.Deg2Rad; g.MaxRateRadPerSec = 7.0;
                    g.MaxTorqueNm = 1300; g.BandwidthHz = 8.0;
                }),
            };

            TestContext.WriteLine($"{"config",-42}  Hold[L]  Priv[L]   Priv/Hold");
            foreach (var (label, tweak) in cases)
            {
                var (hold, priv) = Compare(tweak);
                TestContext.WriteLine($"{label,-42}  {hold,7:F2}  {priv,7:F2}   {(hold > 1e-6 ? priv / hold : 0):F2}");
            }
        }
    }
}
