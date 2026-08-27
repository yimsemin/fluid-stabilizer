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
            ("PrivilegedFullState", () => new PrivilegedFullStateController()),
            ("LQR", () => new LqrSloshController()),
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
        public void NaiveFeedbackControllers_ExciteSlosh_AndWorsenSpill()
        {
            // Central finding (docs/findings.md, design-review §2/§18/§19.1): a
            // naive reactive controller pumps the slosh mode into resonance and
            // spills MORE than doing nothing. This is the gap a learned policy
            // must close, and it is a guard against the plant quietly becoming
            // trivially controllable later.
            var scenario = ScenarioSet.Default().Where(s => s.Name == "ModerateFreeboard").ToArray();
            var results = Evaluator.Run(scenario, Baselines(), Seeds);

            double holdSpill = results.First(r => r.Controller == "Hold").MeanOverflow;
            double pidSpill = results.First(r => r.Controller == "PID").MeanOverflow;
            double sfaSpill = results.First(r => r.Controller == "SpecificForceAlignment").MeanOverflow;

            Assert.Greater(holdSpill, 1e-4, "ModerateFreeboard should actually spill under Hold");
            Assert.Greater(pidSpill, holdSpill * 5.0, "naive PID should spill much more than doing nothing");
            Assert.Greater(sfaSpill, holdSpill * 3.0, "specific-force alignment should spill more than doing nothing");
        }

        [Test]
        public void NoBaselineControllerBeatsHold_OnTheDiscriminatingScenario()
        {
            // Documents finding F1's current state (docs/findings.md): with the
            // equivalent-pendulum slosh model and a 2-axis gimbal, NO baseline —
            // naive PID, smooth feed-forward, or model-based LQR — beats holding
            // the tank rigid on the ModerateFreeboard scenario. Rotating the tank
            // to track the felt tilt excites the slosh more than the alignment
            // helps. This is a project-shaping result; if a later change makes a
            // baseline win here, revisit findings.md and this test.
            var scenario = ScenarioSet.Default().Where(s => s.Name == "ModerateFreeboard").ToArray();
            var results = Evaluator.Run(scenario, Baselines(), Seeds);

            double hold = results.First(r => r.Controller == "Hold").MeanOverflow;
            foreach (var r in results.Where(r => r.Controller != "Hold"))
                Assert.GreaterOrEqual(r.MeanOverflow, hold,
                    $"{r.Controller} unexpectedly beat Hold ({r.MeanOverflow:E3} < {hold:E3}) — update findings.md");
        }

        [Test]
        public void CalmCruise_NobodySpills()
        {
            var calm = ScenarioSet.Default().Where(s => s.Name == "CalmCruise").ToArray();
            var results = Evaluator.Run(calm, Baselines(), Seeds);
            foreach (var r in results)
                Assert.AreEqual(0, r.SpillEpisodes, $"{r.Controller} spilled during calm cruise");
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
