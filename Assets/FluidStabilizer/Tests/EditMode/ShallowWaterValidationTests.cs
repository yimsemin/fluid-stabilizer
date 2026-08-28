using System;
using System.Linq;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// The deciding experiment for finding F1 (docs/findings.md): re-run the
    /// controllability diagnostic and the baseline evaluation with the
    /// shallow-water reference model instead of the pendulum ROM. If tilt
    /// control helps here where it hurt under the pendulum model, F1
    /// interpretation 1 (ROM rotation-coupling too pessimistic) is the answer.
    /// </summary>
    public class ShallowWaterValidationTests
    {
        private const double G = MathD.StandardGravity;
        private const double Dt = 1.0 / 600.0;

        // ---- 1. kinematic controllability: does tank tilt reduce the wall wave? ----

        private static double PeakWave(Func<double, double> aX, Func<double, double, double> tankPitchOf,
            double seconds)
        {
            var p = new SloshParameters
            {
                TankLengthX = 1.2, TankWidthY = 0.7, TankHeightZ = 0.8,
                FillRatio = 0.625, LiquidDensity = 1000.0,
                NaturalFrequencyScale = 1.0, DampingRatio = 0.10,
                QuadraticDamping = 0.9,
                ParticipatingMassScale = 1.0, SurfaceSlopePerPendulumAngle = 1.0,
                SpillDischargeCoefficient = 0.6
            };
            var m = new ShallowWaterSloshModel();
            m.Reset(p, new DeterministicRandom(1));

            double peak = 0, prevPitch = 0, prevRate = 0;
            int n = (int)(seconds / Dt);
            for (int i = 0; i < n; i++)
            {
                double t = i * Dt;
                double a = aX(t);
                double targetPitch = Math.Atan2(a, G);
                double tankPitch = tankPitchOf(t, targetPitch);

                double rate = (tankPitch - prevPitch) / Dt;
                double acc = (rate - prevRate) / Dt;
                prevRate = rate;
                prevPitch = tankPitch;

                var rWT = Double3x3.RotY(tankPitch);
                var o = m.Step(new SloshInput
                {
                    GravityTankFrame = rWT.InverseTimes(new Double3(0, 0, -G)),
                    LinearAccelerationTankFrame = rWT.InverseTimes(new Double3(a, 0, 0)),
                    AngularVelocityTankFrame = new Double3(0, rate, 0),
                    AngularAccelerationTankFrame = new Double3(0, acc, 0),
                    Dt = Dt
                });
                peak = Math.Max(peak, -o.MinFreeboard + (0.8 - 0.5)); // wave height above rest surface
            }
            return peak;
        }

        [Test]
        [Explicit("diagnostic")]
        public void IdealTiltTracking_Helps_UnderShallowWater()
        {
            Func<double, double> aX = t => 3.4 * Math.Sin(2 * Math.PI * 0.5 * t);

            double held = PeakWave(aX, (t, target) => 0.0, 12);
            double ideal = PeakWave(aX, (t, target) => target, 12);
            var lag = new LowPass(2.0); lag.Reset(0);
            double lagged = PeakWave(aX, (t, target) => lag.Step(target, Dt), 12);

            TestContext.WriteLine(
                $"[shallow water] peak wave (m):  held={held:F3}   ideal-tilt={ideal:F3}   lagged-tilt={lagged:F3}");
            TestContext.WriteLine($"  ideal/held = {ideal / held:F2}   lagged/held = {lagged / held:F2}");
        }

        // ---- 2. step-acceleration response vs the pendulum ROM ----

        [Test]
        [Explicit("diagnostic")]
        public void StepAccel_LowVsHighAmplitude_VsPendulum()
        {
            foreach (double a in new[] { 0.5, 1.5, 3.0, 5.0 })
            {
                var (swPeak, swSettle) = StepResponse(new ShallowWaterSloshModel(), a);
                var (pePeak, peSettle) = StepResponse(new PendulumSloshModel(), a);
                TestContext.WriteLine(
                    $"a={a:F1} m/s²  shallow: peak|θ|={swPeak:F3} settle={swSettle:F3}   " +
                    $"pendulum: peak|θ|={pePeak:F3} settle={peSettle:F3}   " +
                    $"peak ratio sw/pe = {swPeak / pePeak:F2}");
            }
        }

        private static (double peak, double settle) StepResponse(ISloshModel m, double a)
        {
            var p = new SloshParameters
            {
                TankLengthX = 1.2, TankWidthY = 0.7, TankHeightZ = 0.8,
                FillRatio = 0.5, LiquidDensity = 1000.0,
                NaturalFrequencyScale = 1.0, DampingRatio = 0.08, QuadraticDamping = 0.9,
                ParticipatingMassScale = 1.0, SurfaceSlopePerPendulumAngle = 1.0,
                SpillDischargeCoefficient = 0.6
            };
            m.Reset(p, new DeterministicRandom(1));
            var input = new SloshInput
            {
                GravityTankFrame = new Double3(0, 0, -G),
                LinearAccelerationTankFrame = new Double3(a, 0, 0),
                AngularVelocityTankFrame = Double3.Zero,
                AngularAccelerationTankFrame = Double3.Zero,
                Dt = Dt
            };
            double peak = 0;
            for (int i = 0; i < 3000; i++)
            {
                m.Step(input);
                peak = Math.Max(peak, Math.Abs(m.SampleGroundTruth().ModeState[0]));
            }
            double settle = Math.Abs(m.SampleGroundTruth().ModeState[0]);
            return (peak, settle);
        }

        // ---- 3. baseline evaluation with the shallow-water model ----

        private static Scenario[] ShallowWaterScenarios() =>
            ScenarioSet.Default().Select(s => new Scenario
            {
                Name = s.Name,
                Config = s.Config,
                Driver = s.Driver,
                Slosh = () => new ShallowWaterSloshModel()
            }).ToArray();

        private static (string, Func<IController>)[] Baselines() => new (string, Func<IController>)[]
        {
            ("Hold", () => new HoldController()),
            ("SpecificForceAlignment", () => new SpecificForceAlignmentController()),
            ("PID", () => new PidController()),
            ("PrivilegedFeedforward", () => new PrivilegedFullStateController()),
        };

        private static readonly ulong[] Seeds = { 1, 2, 3, 4, 5, 6 };

        [Test]
        [Explicit("diagnostic")]
        public void SolverHealth_AcrossScenarios()
        {
            foreach (var sc in ShallowWaterScenarios())
            foreach (var ctrl in new[] { "Hold", "PID" })
            {
                var make = Baselines().First(b => b.Item1 == ctrl).Item2;
                var sw = new ShallowWaterSloshModel();
                var loop = new SimulationLoop(sc.Config(), make(), sc.Driver(1), sw);
                var r = loop.RunEpisode(1);
                TestContext.WriteLine(
                    $"{sc.Name,-26} {ctrl,-5}  spill={r.TotalOverflowVolume * 1000,7:F1} L  " +
                    $"worstFB={r.WorstFreeboard,7:F3}  maxSub={sw.MaxSubStepsUsed,3}  " +
                    $"capHits={sw.SubStepCapHits,6}  vClamp={sw.VelocityClampHits,8}  " +
                    $"breakE={sw.BreakingEnergyRemoved,8:F1} J");

                // the solver must stay well inside its stability envelope in
                // every scenario — no CFL sub-step starvation, no NaN-guard clamp.
                Assert.AreEqual(0, sw.SubStepCapHits, $"{sc.Name}/{ctrl}: CFL sub-step cap hit");
                Assert.AreEqual(0, sw.VelocityClampHits, $"{sc.Name}/{ctrl}: velocity clamp fired");
            }
        }

        [Test]
        [Explicit("diagnostic")]
        public void GridConvergence_HighwayBriskLowFreeboard_Hold()
        {
            // Does the spill verdict hold as the grid is refined? If a coarser
            // and a finer grid disagree wildly the number is mesh-dependent.
            foreach (double cell in new[] { 0.040, 0.025, 0.017 })
            {
                var cfg = new SimulationConfig { EpisodeSeconds = 40.0 };
                cfg.Slosh.FillRatio = 0.66;
                var sw = new ShallowWaterSloshModel(cell);
                var loop = new SimulationLoop(cfg, new HoldController(),
                    new HighwayDriver(1, briskness: 0.9), sw);
                var r = loop.RunEpisode(1);
                var (nx, ny) = sw.GridSize;
                TestContext.WriteLine(
                    $"cell≈{cell:F3}m  grid={nx}×{ny}  spill={r.TotalOverflowVolume * 1000,7:F1} L  " +
                    $"worstFB={r.WorstFreeboard:F3}  maxSub={sw.MaxSubStepsUsed}  vClamp={sw.VelocityClampHits}");
            }
        }

        [Test]
        [Explicit("diagnostic — slow (~25 min)")]
        [Timeout(2_400_000)]
        public void BaselineEvaluation_UnderShallowWater_PrintsReport()
        {
            var results = Evaluator.Run(ShallowWaterScenarios(), Baselines(), Seeds);
            TestContext.WriteLine("=== BASELINES UNDER SHALLOW-WATER MODEL ===");
            TestContext.WriteLine(Evaluator.FormatReport(results));
        }
    }
}
