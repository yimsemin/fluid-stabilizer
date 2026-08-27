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
        public void TiltControl_ClearlyBeatsHold_OnRealisticBriskDriving()
        {
            // Finding F1 follow-up (docs/findings.md): with a realistic
            // band-limited highway driver, tilting the tank to track the felt
            // specific force works — the analytic baselines spill far less than
            // holding the tank rigid. (Under the aggressive/adversarial driver
            // they still struggle — see the next test.)
            var scenario = ScenarioSet.Default().Where(s => s.Name == "HighwayBriskLowFreeboard").ToArray();
            var results = Evaluator.Run(scenario, Baselines(), Seeds);

            double hold = results.First(r => r.Controller == "Hold").MeanOverflow;
            double pid = results.First(r => r.Controller == "PID").MeanOverflow;
            double sfa = results.First(r => r.Controller == "SpecificForceAlignment").MeanOverflow;

            Assert.Greater(hold, 1e-3, "the scenario should actually challenge Hold");
            Assert.Less(pid, hold * 0.35, "PID should spill far less than Hold on realistic driving");
            Assert.Less(sfa, hold * 0.35, "specific-force alignment should spill far less than Hold");
        }

        [Test]
        public void NaiveTiltControl_StillExcitesSlosh_UnderAggressiveDriving()
        {
            // The remaining hard case (docs/findings.md F1): under sharp,
            // broadband maneuvers the naive baselines still pump the slosh mode.
            // This is the gap a learned/anticipatory policy must close, and a
            // guard against the plant quietly becoming trivially controllable.
            var scenario = ScenarioSet.Default().Where(s => s.Name == "AggressiveRandom").ToArray();
            var results = Evaluator.Run(scenario, Baselines(), Seeds);

            double holdSlosh = results.First(r => r.Controller == "Hold").MeanSloshEnergyIntegral;
            double sfaSlosh = results.First(r => r.Controller == "SpecificForceAlignment").MeanSloshEnergyIntegral;
            double pidSlosh = results.First(r => r.Controller == "PID").MeanSloshEnergyIntegral;

            Assert.Greater(sfaSlosh, holdSlosh, "SFA still excites slosh under aggressive driving");
            Assert.Greater(pidSlosh, holdSlosh, "PID still excites slosh under aggressive driving");
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
