using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    public class RoadModelTests
    {
        private static IRandomSource Rng(ulong seed) => new DeterministicRandom(seed).Fork("road");

        [Test]
        public void Disabled_IsExactlyFlat()
        {
            var m = new RoadModel(new RoadConfig { Enabled = false });
            m.Reset(Rng(1));
            for (double s = 0; s < 5000; s += 3.17)
            {
                RoadInput r = m.Sample(s);
                Assert.AreEqual(0.0, r.Grade, 0.0);
                Assert.AreEqual(0.0, r.Bank, 0.0);
                Assert.AreEqual(0.0, r.SurfaceHeight, 0.0);
                Assert.AreEqual(0.0, r.Curvature, 0.0);
            }
        }

        [Test]
        public void SameSeed_SameProfile_DifferentSeed_Differs()
        {
            double Checksum(ulong seed)
            {
                var m = new RoadModel(new RoadConfig { Enabled = true });
                m.Reset(Rng(seed));
                double c = 0;
                for (double s = 0; s < 3000; s += 0.5)
                {
                    RoadInput r = m.Sample(s);
                    c = c * 1.0000001 + r.Grade * 3.0 + r.Bank * 5.0 + r.SurfaceHeight + r.Curvature * 111.0;
                }
                return c;
            }

            Assert.AreEqual(Checksum(42), Checksum(42));
            Assert.AreNotEqual(Checksum(42), Checksum(43));
        }

        [Test]
        public void Sampling_IsIndependentOfStepSize()
        {
            // Lazy event generation must not depend on how the distance axis is
            // chunked — coarse and fine sampling must agree at shared points.
            var coarse = new RoadModel(new RoadConfig { Enabled = true });
            var fine = new RoadModel(new RoadConfig { Enabled = true });
            coarse.Reset(Rng(7));
            fine.Reset(Rng(7));

            for (double s = 0; s < 1500; s += 5.0)
            {
                // advance the fine model through the gap
                for (double f = s - 5.0 + 1.0; f < s; f += 1.0) if (f > 0) fine.Sample(f);
                RoadInput a = coarse.Sample(s);
                RoadInput b = fine.Sample(s);
                Assert.AreEqual(a.SurfaceHeight, b.SurfaceHeight, 1e-9, $"mismatch at s={s}");
                Assert.AreEqual(a.Grade, b.Grade, 1e-12);
            }
        }

        [Test]
        public void Roughness_RmsIsCloseToConfigured()
        {
            var cfg = new RoadConfig
            {
                Enabled = true,
                RoughnessRmsM = 0.008,
                EventsPerKm = 0.0,        // isolate the continuous roughness
                GradeAmplitudeRad = 0.0,
                BankAmplitudeRad = 0.0,
            };
            var m = new RoadModel(cfg);
            m.Reset(Rng(3));

            double sumSq = 0; int n = 0;
            for (double s = 0; s < 4000; s += 0.13) { double h = m.Sample(s).SurfaceHeight; sumSq += h * h; n++; }
            double rms = Math.Sqrt(sumSq / n);

            Assert.That(rms, Is.InRange(0.004, 0.016), $"roughness RMS {rms:F4} m should be near the 8 mm target");
        }

        [Test]
        public void Severity_ScalesRoughness_NotGrade()
        {
            double RoughRms(double severity)
            {
                var cfg = new RoadConfig { Enabled = true, EventsPerKm = 0.0, Severity = severity };
                var m = new RoadModel(cfg);
                m.Reset(Rng(9));
                double sumSq = 0; int n = 0;
                for (double s = 0; s < 3000; s += 0.2) { double h = m.Sample(s).SurfaceHeight; sumSq += h * h; n++; }
                return Math.Sqrt(sumSq / n);
            }

            Assert.Greater(RoughRms(2.0), RoughRms(0.5) * 2.0, "severity scales surface roughness roughly linearly");
        }

        [Test]
        public void Grade_StaysWithinAmplitude()
        {
            var cfg = new RoadConfig { Enabled = true, GradeAmplitudeRad = 0.05 };
            var m = new RoadModel(cfg);
            m.Reset(Rng(11));
            double peak = 0;
            for (double s = 0; s < 6000; s += 1.0) peak = Math.Max(peak, Math.Abs(m.Sample(s).Grade));
            Assert.LessOrEqual(peak, cfg.GradeAmplitudeRad + 1e-9);
            Assert.Greater(peak, 0.5 * cfg.GradeAmplitudeRad, "the grade should actually swing");
        }

        [Test]
        public void DiscreteEvents_ProduceLocalisedExcursions()
        {
            var cfg = new RoadConfig
            {
                Enabled = true,
                RoughnessRmsM = 0.0,
                GradeAmplitudeRad = 0.0,
                BankAmplitudeRad = 0.0,
                EventsPerKm = 30.0,
                EventHeightM = 0.05,
            };
            var m = new RoadModel(cfg);
            m.Reset(Rng(5));

            int nonZero = 0, total = 0;
            double peak = 0;
            for (double s = 0; s < 3000; s += 0.05)
            {
                double h = Math.Abs(m.Sample(s).SurfaceHeight);
                if (h > 1e-6) nonZero++;
                peak = Math.Max(peak, h);
                total++;
            }
            double frac = nonZero / (double)total;
            Assert.That(frac, Is.InRange(0.002, 0.3), "events are localised — the road is flat between them");
            Assert.Greater(nonZero, 20, "there are several events along a 3 km stretch");
            Assert.Greater(peak, 0.02, "an event reaches a good fraction of its configured height");
        }

        [Test]
        public void Curvature_SwingsBothWays_WithinAmplitude_AndBendsTheTruckPath()
        {
            var cfg = new RoadConfig { Enabled = true, CurvatureAmplitudeRadPerM = 0.004 };
            var m = new RoadModel(cfg);
            m.Reset(Rng(21));
            double peak = 0, minK = 0, maxK = 0;
            for (double s = 0; s < 6000; s += 1.0)
            {
                double k = m.Sample(s).Curvature;
                peak = Math.Max(peak, Math.Abs(k));
                minK = Math.Min(minK, k);
                maxK = Math.Max(maxK, k);
            }
            Assert.LessOrEqual(peak, cfg.CurvatureAmplitudeRadPerM + 1e-9);
            Assert.Greater(peak, 0.4 * cfg.CurvatureAmplitudeRadPerM, "the road should actually curve");
            Assert.Less(minK, -1e-4, "curves left");
            Assert.Greater(maxK, 1e-4, "curves right");

            // In the loop: with the road on, the truck yaws even with zero steer
            // input (it follows the curve).
            var sc = new SimulationConfig { EpisodeSeconds = 12.0 };
            sc.Road.Enabled = true;
            var loop = new SimulationLoop(sc, new HoldController(),
                new ConstantDriver(new DriverControls { Throttle = 0.2 }), new PendulumSloshModel());
            loop.BeginEpisode(21);
            double maxYaw = 0;
            while (loop.StepPhysics())
                maxYaw = Math.Max(maxYaw, Math.Abs(loop.VehicleState.AngularVelocityBody.Z));
            Assert.Greater(maxYaw, 0.02, "a truck with no steer input still follows the road's curve");
        }

        [Test]
        public void LaneModel_DriverHasLateralAuthority_ButCannotLeaveTheRoad()
        {
            SimulationLoop Loop(IDriverInput d)
            {
                var c = new SimulationConfig { EpisodeSeconds = 14.0 };
                c.Road.Enabled = true;
                var l = new SimulationLoop(c, new HoldController(), d, new PendulumSloshModel());
                l.BeginEpisode(31);
                return l;
            }

            // Hands off: the truck follows the road, staying near the centre.
            var ho = Loop(new ConstantDriver(new DriverControls { Throttle = 0.15 }));
            double maxOffHandsOff = 0;
            while (ho.StepPhysics()) maxOffHandsOff = Math.Max(maxOffHandsOff, Math.Abs(ho.VehicleState.LateralOffsetFromRoadCentre));
            Assert.Less(maxOffHandsOff, 3.0, "a hands-off truck should track the road, not wander across it");

            // Holding a steer input moves the truck across lanes...
            var steer = Loop(new ConstantDriver(new DriverControls { Throttle = 0.15, Steer = 0.25 }));
            double maxOffSteer = 0;
            while (steer.StepPhysics()) maxOffSteer = Math.Max(maxOffSteer, Math.Abs(steer.VehicleState.LateralOffsetFromRoadCentre));
            Assert.Greater(maxOffSteer, 3.0, "the driver can move the truck laterally across the road");

            // ...but the firm edge stops it leaving (road half-width = 7 m here).
            double half = new RoadConfig().RoadHalfWidthM;
            Assert.Less(maxOffSteer, half + 1.5, "the road edge is a firm wall — the truck stays on the road");
        }

        [Test]
        public void InLoop_RoughRoad_AddsSloshEnergy_AndIsDeterministic()
        {
            EpisodeResult Run(bool road, out double checksum)
            {
                var cfg = new SimulationConfig { EpisodeSeconds = 18.0 };
                cfg.Slosh.FillRatio = 0.6;
                cfg.Road.Enabled = road;
                cfg.Road.Severity = 1.5;
                var loop = new SimulationLoop(cfg, new HoldController(),
                    new HighwayDriver(0, briskness: 0.4), new PendulumSloshModel());
                loop.BeginEpisode(4);
                double c = 0;
                while (loop.StepPhysics())
                {
                    var gt = loop.LatestGroundTruth;
                    c = c * 1.0000001 + gt.TankSpecificForceTankFrame.Z + gt.RoadSurfaceHeight * 10.0;
                }
                checksum = c;
                return loop.Result();
            }

            EpisodeResult flat = Run(false, out _);
            EpisodeResult rough = Run(true, out double roughSum);
            Run(true, out double roughSum2);

            Assert.AreEqual(roughSum, roughSum2, 0.0, "a road episode is bit-reproducible");
            Assert.Greater(rough.SloshEnergyIntegral, flat.SloshEnergyIntegral * 1.2,
                "driving the same route on a rough road puts more energy into the liquid");
        }

        // ---- left/right wheel-track asymmetry (묶음 2a) ----

        [Test]
        public void WheelTracks_DifferAtShortWavelength_ButAverageToTheCentre()
        {
            var cfg = new RoadConfig { Enabled = true, Severity = 1.5, WheelTrackRoughnessFrac = 0.5 };
            var m = new RoadModel(cfg);
            m.Reset(Rng(13));

            double maxDiff = 0.0, avgErr = 0.0;
            int n = 0;
            for (double s = 0; s < 3000; s += 0.17)
            {
                RoadInput r = m.Sample(s);
                maxDiff = Math.Max(maxDiff, Math.Abs(r.SurfaceHeightLeft - r.SurfaceHeightRight));
                avgErr = Math.Max(avgErr, Math.Abs(0.5 * (r.SurfaceHeightLeft + r.SurfaceHeightRight) - r.SurfaceHeight));
                n++;
            }
            Assert.Greater(maxDiff, 3e-3, "the two wheel tracks must actually decorrelate");
            Assert.Less(avgErr, 1e-9, "SurfaceHeight must be exactly the mean of the two tracks");
        }

        [Test]
        public void WheelTracks_AreIdentical_WhenTheDifferentialFractionIsZero()
        {
            var cfg = new RoadConfig { Enabled = true, Severity = 1.5, WheelTrackRoughnessFrac = 0.0 };
            var m = new RoadModel(cfg);
            m.Reset(Rng(13));
            for (double s = 0; s < 2000; s += 0.31)
            {
                RoadInput r = m.Sample(s);
                Assert.AreEqual(r.SurfaceHeightLeft, r.SurfaceHeightRight, 1e-12, "no differential → symmetric road");
            }
        }

        [Test]
        public void OneSidedRoadBump_RollsTheTruck_MoreThanASymmetricRoad()
        {
            double RollRateRms(double diffFrac)
            {
                var cfg = new SimulationConfig { EpisodeSeconds = 20.0 };
                cfg.Slosh.FillRatio = 0.6;
                cfg.Road.Enabled = true;
                cfg.Road.Severity = 1.5;
                cfg.Road.EventsPerKm = 45.0;
                cfg.Road.EventHeightM = 0.07;
                cfg.Road.WheelTrackRoughnessFrac = diffFrac;
                var loop = new SimulationLoop(cfg, new HoldController(),
                    new ConstantDriver(new DriverControls { Throttle = 0.18 }), new PendulumSloshModel());
                loop.BeginEpisode(4);
                double sumSq = 0.0; int n = 0;
                while (loop.StepPhysics())
                {
                    double rr = loop.VehicleState.AngularVelocityBody.X;
                    sumSq += rr * rr; n++;
                }
                return Math.Sqrt(sumSq / n);
            }

            double symmetric = RollRateRms(0.0);
            double asymmetric = RollRateRms(0.8);
            Assert.Greater(asymmetric, symmetric * 1.3,
                "independent left/right wheel-track roughness + one-sided potholes roll the body more");
        }

        // ---- tyre-slip proxy (묶음 2b) ----

        [Test]
        public void LowGrip_HardManoeuvre_Slips_AndFishtails()
        {
            // Same aggressive drive; vary grip and whether the slip fishtail is on.
            (EpisodeResult res, double slipIntegral, double yawRms) Run(double grip, double fishtail)
            {
                var cfg = new SimulationConfig { EpisodeSeconds = 22.0 };
                cfg.Slosh.FillRatio = 0.62;
                cfg.Vehicle.FrictionLimit *= grip;
                cfg.Vehicle.MaxBrake *= grip;
                cfg.Vehicle.SlipYawWander = fishtail;
                var loop = new SimulationLoop(cfg, new HoldController(),
                    new RandomManeuverDriver(2, aggression: 0.7), new PendulumSloshModel());
                loop.BeginEpisode(2);
                double slip = 0.0, sumSq = 0.0; int n = 0;
                while (loop.StepPhysics())
                {
                    slip += loop.VehicleState.TyreSlip * cfg.PhysicsStepSeconds;
                    double y = loop.VehicleState.AngularVelocityBody.Z;
                    sumSq += y * y; n++;
                }
                return (loop.Result(), slip, Math.Sqrt(sumSq / n));
            }

            var dry = Run(1.0, 0.35);
            var ice = Run(0.4, 0.35);
            var iceNoFishtail = Run(0.4, 0.0);
            var iceRepeat = Run(0.4, 0.35);

            Assert.AreEqual(ice.slipIntegral, iceRepeat.slipIntegral, 0.0, "a low-grip episode is bit-reproducible");
            Assert.Greater(ice.slipIntegral, dry.slipIntegral * 2.0 + 0.3, "the same driving slides much more on ice");
            Assert.Greater(ice.yawRms, iceNoFishtail.yawRms * 1.15,
                "the fishtail adds yaw wander the truck otherwise would not have");
            Assert.Greater(ice.res.SloshEnergyIntegral, iceNoFishtail.res.SloshEnergyIntegral,
                "the fishtail slops the tank more than a clean low-grip slide");
        }
    }
}
