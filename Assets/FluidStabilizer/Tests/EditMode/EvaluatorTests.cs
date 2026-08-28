using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    public class BaselineControllerTests
    {
        private static (string, Func<IController>)[] Baselines() => new (string, Func<IController>)[]
        {
            ("Hold", () => new HoldController()),
            ("SpecificForceAlignment", () => new SpecificForceAlignmentController()),
            ("PID", () => new PidController()),
            ("PrivilegedFeedforward", () => new PrivilegedFullStateController()),
            // LQR is SHELVED as a baseline (see docs/findings.md F1): its linear
            // design model (ideal rate follower) is too far from the real
            // servo + delay + angle-command plant, so it resonates. AxisLqr's
            // math is still tested (AxisLqrTests); a proper MPC on the real
            // actuator model is future work.
        };

        private static readonly ulong[] Seeds = { 1, 2, 3, 4, 5, 6 };

        [Test]
        public void Report_PrintsAndIsDeterministic()
        {
            var scenarios = ScenarioSet.Default();

            var a = Evaluator.Run(scenarios, Baselines(), Seeds);
            var b = Evaluator.Run(scenarios, Baselines(), Seeds);

            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].MeanOverflow, b[i].MeanOverflow, 1e-12, "eval not deterministic");
                Assert.AreEqual(a[i].MeanSloshEnergyIntegral, b[i].MeanSloshEnergyIntegral, 1e-12);
            }

            TestContext.WriteLine(Evaluator.FormatReport(a));
        }

        [Test]
        public void RigidHold_NotBeatenByReactiveTilt_OnLowFreeboardHighway()
        {
            // Finding F4 (docs/findings.md): with the locked-joint Hold baseline
            // (decision D5) the rigid tank does not spill on brisk highway
            // driving, and the reactive analytic tilt laws — which add slosh
            // energy by rotating the tank — spill where Hold does not. F2's
            // "tilt beats Hold 15x here" was an artefact of a sagging servo Hold.
            var scenario = ScenarioSet.Default().Where(s => s.Name == "HighwayBriskLowFreeboard").ToArray();
            var results = Evaluator.Run(scenario, Baselines(), Seeds);

            double hold = results.First(r => r.Controller == "Hold").MeanOverflow;
            double pid = results.First(r => r.Controller == "PID").MeanOverflow;
            double sfa = results.First(r => r.Controller == "SpecificForceAlignment").MeanOverflow;

            // MeanOverflow is m³ (× 1000 = litres).
            Assert.LessOrEqual(hold, 1e-4, "a rigidly locked tank should not spill on brisk highway driving");
            Assert.Greater(pid, 5e-5, "reactive tilt control adds slosh and spills where rigid Hold does not");
            Assert.Greater(sfa, 5e-5, "reactive tilt control adds slosh and spills where rigid Hold does not");
        }

        [Test]
        public void ReactiveTilt_LosesToRigidHold_UnderAggressiveHighFill()
        {
            // Finding F4: in the hard regime the actuator is authority-limited
            // (decision D5 — deliberate) and reactive tilt tracking saturates and
            // pumps the slosh, spilling far more than a rigid tank. This is the
            // gap a learned/anticipatory policy has to close.
            var scenario = ScenarioSet.Default().Where(s => s.Name == "HighFillAggressive").ToArray();
            var results = Evaluator.Run(scenario, Baselines(), Seeds);

            double hold = results.First(r => r.Controller == "Hold").MeanOverflow;
            double sfa = results.First(r => r.Controller == "SpecificForceAlignment").MeanOverflow;
            double pid = results.First(r => r.Controller == "PID").MeanOverflow;

            // MeanOverflow is m³; Hold ≈ 0.010 m³ here, tilt controllers ≈ 0.04–0.10.
            Assert.Greater(hold, 3e-3, "the scenario challenges even a rigid tank");
            Assert.Greater(sfa, hold * 2.0, "reactive tilt control spills far more than holding rigid");
            Assert.Greater(pid, hold * 2.0, "reactive tilt control spills far more than holding rigid");
        }

        [Test]
        public void HighwayCalm_NobodySpills()
        {
            var calm = ScenarioSet.Default().Where(s => s.Name == "HighwayCalm").ToArray();
            var results = Evaluator.Run(calm, Baselines(), Seeds);
            foreach (var r in results)
                Assert.AreEqual(0, r.SpillEpisodes, $"{r.Controller} spilled during calm highway driving");
        }

        [Test]
        public void PrivilegedController_ReceivesPrivilegedObservation()
        {
            // Sanity: the loop actually feeds privileged data (slosh angle non-zero
            // once excited) — a probe controller records what it sees.
            double maxSloshSeen = 0;
            var probe = new ProbeController(p => maxSloshSeen = Math.Max(maxSloshSeen, Math.Abs(p.SloshPitchAngle)));

            var loop = new SimulationLoop(new SimulationConfig { EpisodeSeconds = 15.0 },
                probe, new ScriptedManeuverDriver(), new PendulumSloshModel());
            loop.RunEpisode(1);

            Assert.Greater(maxSloshSeen, 0.02);
        }

        private sealed class ProbeController : IPrivilegedController
        {
            private readonly Action<PrivilegedObservation> _sink;
            public ProbeController(Action<PrivilegedObservation> sink) { _sink = sink; }
            public string Name => "Probe";
            public void Reset() { }
            public StabilizerCommand ComputeCommand(in SensorBundle s) => StabilizerCommand.Hold(0, s.StampSeconds);
            public StabilizerCommand ComputeCommand(in SensorBundle s, in PrivilegedObservation p)
            {
                _sink(p);
                return StabilizerCommand.Hold(0, s.StampSeconds);
            }
        }
    }
}
