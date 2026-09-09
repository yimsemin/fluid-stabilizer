using System;
using System.Collections.Generic;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.Eval;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// experiment-plan.md step 2c: the recorded-drive evaluation must be
    /// deterministic and must produce one row per (drive × controller × model).
    /// </summary>
    public class TraceEvaluatorTests
    {
        private static DriverTrace Rec(ulong seed, double fill)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 6.0 };
            cfg.Slosh.FillRatio = fill;
            var trace = new DriverTrace
            {
                Label = "drive-" + seed, Seed = seed, StepSeconds = cfg.PhysicsStepSeconds, FillRatio = fill
            };
            var loop = new SimulationLoop(cfg, new HoldController(),
                new RecordingDriver(new RandomManeuverDriver(seed, 0.7), trace), new PendulumSloshModel());
            loop.BeginEpisode(seed);
            while (loop.StepPhysics()) { }
            return trace;
        }

        [Test]
        public void ProducesOneRowPerCombination_AndIsDeterministic()
        {
            var traces = new List<DriverTrace> { Rec(1, 0.62), Rec(2, 0.72) };
            var controllers = new List<(string, Func<SimulationConfig, IController>)>
            {
                ("Rigid (Hold)", _ => new HoldController()),
                ("PID", _ => new PidController()),
            };
            var models = new List<(string, Func<ISloshModel>)> { ("pendulum", () => new PendulumSloshModel()) };

            var a = TraceEvaluator.Run(traces, controllers, models);
            var b = TraceEvaluator.Run(traces, controllers, models);

            Assert.AreEqual(2 * 2 * 1, a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].Trace, b[i].Trace);
                Assert.AreEqual(a[i].Controller, b[i].Controller);
                Assert.AreEqual(a[i].SpillLitres, b[i].SpillLitres, 0.0, "trace eval not deterministic");
            }

            string report = TraceEvaluator.FormatReport(a);
            StringAssert.Contains("drive-1", report);
            StringAssert.Contains("drive-2", report);
            StringAssert.Contains("Rigid (Hold)", report);
            StringAssert.Contains("slosh model: pendulum", report);
        }

        [Test]
        public void EachRow_MatchesAStandaloneReplayOfThatCombination()
        {
            var trace = Rec(5, 0.7);
            var controllers = new List<(string, Func<SimulationConfig, IController>)>
            {
                ("Rigid (Hold)", _ => new HoldController()),
            };
            var models = new List<(string, Func<ISloshModel>)> { ("pendulum", () => new PendulumSloshModel()) };

            var row = TraceEvaluator.Run(new[] { trace }, controllers, models)[0];

            var loop = new SimulationLoop(trace.ToConfig(), new HoldController(),
                new RecordedTraceDriver(trace), new PendulumSloshModel());
            var r = loop.RunEpisode(trace.Seed);

            Assert.AreEqual(r.TotalOverflowVolume * 1000.0, row.SpillLitres, 0.0);
            Assert.AreEqual(r.WorstFreeboard, row.WorstFreeboard, 0.0);
        }
    }
}
