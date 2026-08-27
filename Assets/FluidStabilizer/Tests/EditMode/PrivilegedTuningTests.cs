using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    /// <summary>Diagnostic traces for the baseline controllers. Not a pass/fail gate.</summary>
    [Explicit("diagnostic")]
    public class PrivilegedTuningTests
    {
        [Test]
        public void TraceOneEpisode()
        {
            foreach (var (label, make) in new (string, Func<IController>)[]
                     {
                         ("Hold", () => new HoldController()),
                         ("SFA",  () => new SpecificForceAlignmentController()),
                         ("Priv", () => new PrivilegedFullStateController()),
                         ("LQR",  () => new LqrSloshController()),
                     })
            {
                var cfg = new SimulationConfig { EpisodeSeconds = 10.0 };
                cfg.Slosh.FillRatio = 0.625;
                var loop = new SimulationLoop(cfg, make(), new RandomManeuverDriver(7, 0.6), new PendulumSloshModel());
                loop.BeginEpisode(7);
                TestContext.WriteLine($"\n--- {label} ---   t   aLong  aLat | pitch  roll |sloshP sloshR| spillL");
                double nextLog = 0;
                while (loop.StepPhysics())
                {
                    if (loop.SimTime >= nextLog)
                    {
                        nextLog += 0.5;
                        var gt = loop.LatestGroundTruth;
                        var pv = loop.BuildPrivilegedObservation();
                        TestContext.WriteLine($"  {loop.SimTime,5:F1}  {gt.VehicleLongAccel,5:F1} {gt.VehicleLatAccel,5:F1} | " +
                            $"{gt.PitchAngle,6:F3} {gt.RollAngle,6:F3} | {pv.SloshPitchAngle,6:F3} {pv.SloshRollAngle,6:F3} | {gt.CumulativeOverflowVolume * 1000,6:F2}");
                    }
                }
                TestContext.WriteLine($"  total spill = {loop.Result().TotalOverflowVolume * 1000:F2} L, sloshE∫ = {loop.Result().SloshEnergyIntegral:F0}");
            }
        }
    }
}
