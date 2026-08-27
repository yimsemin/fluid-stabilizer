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
    /// Exploratory: sweep the privileged controller's slosh-damping gain signs to
    /// find the combination that actually removes slosh energy. Prints a table;
    /// not a pass/fail gate beyond "some setting beats Hold".
    /// </summary>
    [Explicit("diagnostic sweep — run manually")]
    public class PrivilegedTuningTests
    {
        private static double RunSlosh(Func<IController> make, double fill = 0.6)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 25.0 };
            cfg.Slosh.FillRatio = fill;
            var loop = new SimulationLoop(cfg, make(), new RandomManeuverDriver(7, 0.6), new PendulumSloshModel());
            var r = loop.RunEpisode(7);
            return r.SloshEnergyIntegral;
        }

        [Test]
        public void SweepPrivilegedGains()
        {
            foreach (double fill in new[] { 0.5, 0.625 })
            {
                double hold = RunSlosh(() => new HoldController(), fill);
                double holdSpill = RunSpill(() => new HoldController(), fill);
                TestContext.WriteLine($"\n=== fill {fill}: Hold sloshE∫={hold:F0}  spill={holdSpill*1000:F2} L ===");

                foreach (double kTrack in new[] { 1.0, 2.0, 3.5, 5.0 })
                foreach (double kSlosh in new[] { 0.0, 0.5, 1.0, 2.0 })
                foreach (double slew in new[] { 4.0, 8.0, 20.0 })
                {
                    var e = RunBoth(() => new PrivilegedFullStateController(kTrack, kSlosh, slew), fill);
                    string tag = double.IsNaN(e.slosh) || e.slosh > hold ? "WORSE" : (e.slosh < 0.7 * hold ? "GOOD" : "ok");
                    TestContext.WriteLine($"kTrack={kTrack,4} kSloshRate={kSlosh} slew={slew,4}  ->  sloshE∫={e.slosh,9:F0}  spill={e.spill*1000,7:F2}L  {tag}");
                }
            }
        }

        private static double RunSpill(Func<IController> make, double fill)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 25.0 };
            cfg.Slosh.FillRatio = fill;
            return new SimulationLoop(cfg, make(), new RandomManeuverDriver(7, 0.6), new PendulumSloshModel())
                .RunEpisode(7).TotalOverflowVolume;
        }

        [Test]
        public void TraceOneEpisode()
        {
            foreach (var (label, make) in new (string, Func<IController>)[]
                     {
                         ("Hold", () => new HoldController()),
                         ("Priv", () => new PrivilegedFullStateController()),
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
                            $"{gt.PitchAngle,6:F3} {gt.RollAngle,6:F3} | {pv.SloshPitchAngle,6:F3} {pv.SloshRollAngle,6:F3} | {gt.CumulativeOverflowVolume*1000,6:F2}");
                    }
                }
            }
        }

        private static (double slosh, double spill) RunBoth(Func<IController> make, double fill)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 25.0 };
            cfg.Slosh.FillRatio = fill;
            var r = new SimulationLoop(cfg, make(), new RandomManeuverDriver(7, 0.6), new PendulumSloshModel())
                .RunEpisode(7);
            return (r.SloshEnergyIntegral, r.TotalOverflowVolume);
        }
    }
}
