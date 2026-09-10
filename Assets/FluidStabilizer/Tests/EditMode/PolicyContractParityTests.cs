using System;
using System.Collections.Generic;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Rl;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// The contract check (작업2): a policy trained in <see cref="RlEnvironment"/>
    /// and the same policy served through the bridge must mean the same thing by
    /// the same numbers. Before <see cref="PolicyContract"/> they did not —
    /// training composed <c>baseline + policy·0.35·maxRate</c> behind a lock gate,
    /// the bridge sent <c>policy·maxRate</c> with no baseline and no gate.
    ///
    /// Equal encoder classes are not evidence of equal pipelines, so these compare
    /// the emitted commands and the resulting spill, not the code path.
    /// </summary>
    public class PolicyContractParityTests
    {
        private static SimulationConfig Cfg()
        {
            var cfg = new SimulationConfig { EpisodeSeconds = 6.0 };
            cfg.Slosh.FillRatio = 0.62;
            cfg.Road.Enabled = true;
            cfg.Validate();
            return cfg;
        }

        // A deterministic "policy": always the same action, so any divergence is the
        // pipeline's, not the policy's.
        private const float ActP = 0.4f, ActR = -0.25f;

        // A lock request that actually toggles: positive here, so an explicit-lock
        // contract locks and a dead-band one ignores it. Either way both paths must
        // agree — that is the whole point.
        private const float ActLock = 0.6f;

        [TestCase("none", false, "none")]
        [TestCase("hold", true, "deadband")]
        [TestCase("sfa", true, "deadband")]
        [TestCase("hold", true, "explicit")]
        public void TrainingAndServedPaths_ProduceTheSameCommandsAndSpill(
            string baselineId, bool lockCapable, string lockMode)
        {
            SimulationConfig cfg = Cfg();
            bool residual = baselineId != "none";
            double residualScale = residual ? 0.35 * cfg.Gimbal.MaxRateRadPerSec : double.NaN;

            // ---- path A: the training environment ----
            var env = new RlEnvironment(
                cfg, seed => new HighwayDriver(seed, briskness: 0.6), () => new PendulumSloshModel(),
                RewardWeights.Default(), frameStackLength: 6,
                baselineFactory: residual ? () => PolicyContract.MakeBaseline(baselineId) : (Func<IController>)null,
                residualRateScaleRadPerSec: residualScale,
                lockCapable: lockCapable,
                baselineId: residual ? baselineId : null,
                lockMode: lockMode);

            env.Reset(7UL);
            var trainObs = new List<float[]>();
            while (true)
            {
                RlStepResult r = env.Step(ActP, ActR, ActLock);
                trainObs.Add(r.Observation);
                if (r.Terminated || r.Truncated) break;
            }
            EpisodeResult trained = env.Summary();

            // ---- path B: the same policy served over the bridge ----
            PolicyContract contract = env.Contract;
            Assert.AreEqual(residual ? baselineId : "none", contract.Baseline);
            Assert.AreEqual(lockCapable, contract.LockCapable);

            int obsSize = ObservationEncoder.SingleFrameSize * contract.FrameStack;
            float[] reply = contract.ActionSize == 3
                ? new[] { ActP, ActR, ActLock }
                : new[] { ActP, ActR };
            using var server = new FakeContractServer(obsSize, reply, contract: contract.Encode());

            using var bridge = new RemotePolicyController(
                "127.0.0.1", server.Port, contract.FrameStack,
                cfg.Gimbal.MaxRateRadPerSec, timeoutMs: 4000, gimbal: cfg.Gimbal);

            var loop = new SimulationLoop(cfg, bridge,
                new HighwayDriver(7UL, briskness: 0.6), new PendulumSloshModel());
            EpisodeResult served = loop.RunEpisode(7UL);

            Assert.AreEqual("policy-ok", bridge.ExecutionStatus,
                "the bridge must have actually run the policy, not fallen back");
            Assert.AreEqual(contract, bridge.Contract, "the bridge executed a different contract");

            // The claim that matters: identical plant outcome.
            Assert.AreEqual(trained.TotalOverflowVolume, served.TotalOverflowVolume, 1e-12,
                $"[{baselineId}] training and served paths spilled different amounts");
            Assert.AreEqual(trained.WorstFreeboard, served.WorstFreeboard, 1e-12);
            Assert.AreEqual(trained.MeanAbsActionRate, served.MeanAbsActionRate, 1e-12,
                "same actions must produce the same commanded rates");
            Assert.AreEqual(trained.PhysicsSteps, served.PhysicsSteps);
        }

        [Test]
        public void ServedPolicy_WithoutAContract_RunsLegacyPathAndIsNotCountedAsANormalAiResult()
        {
            SimulationConfig cfg = Cfg();
            int obsSize = ObservationEncoder.SingleFrameSize * 6;
            using var server = new FakeContractServer(obsSize, new[] { 0.1f, 0.1f }, contract: null);
            using var bridge = new RemotePolicyController(
                "127.0.0.1", server.Port, 6, cfg.Gimbal.MaxRateRadPerSec, timeoutMs: 4000, gimbal: cfg.Gimbal);

            var loop = new SimulationLoop(cfg, bridge,
                new HighwayDriver(3UL, briskness: 0.5), new PendulumSloshModel());
            loop.RunEpisode(3UL);

            Assert.AreEqual("none", bridge.Contract.Baseline, "no contract → the legacy plain path");
            StringAssert.Contains("legacy-assumed", bridge.PolicySettings);
        }

        [Test]
        public void ServedPolicy_WithAMismatchedActuatorCeiling_IsRefusedRatherThanRescaled()
        {
            SimulationConfig cfg = Cfg();
            var wrong = PolicyContract.Residual(cfg.Gimbal, "hold");
            wrong.MaxRateRadPerSec = cfg.Gimbal.MaxRateRadPerSec * 2.0;   // trained on a different actuator

            int obsSize = ObservationEncoder.SingleFrameSize * 6;
            using var server = new FakeContractServer(obsSize, new[] { 0.2f, 0f }, contract: wrong.Encode());
            using var bridge = new RemotePolicyController(
                "127.0.0.1", server.Port, 6, cfg.Gimbal.MaxRateRadPerSec, timeoutMs: 4000, gimbal: cfg.Gimbal);

            var loop = new SimulationLoop(cfg, bridge,
                new HighwayDriver(5UL, briskness: 0.5), new PendulumSloshModel());
            loop.RunEpisode(5UL);

            // Refused at handshake → never ran → Hold, and reported as such.
            Assert.AreEqual("hold-fallback", bridge.ExecutionStatus);
        }
    }
}
