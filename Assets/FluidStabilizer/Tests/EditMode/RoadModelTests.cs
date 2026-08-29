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
                    c = c * 1.0000001 + r.Grade * 3.0 + r.Bank * 5.0 + r.SurfaceHeight;
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
    }
}
