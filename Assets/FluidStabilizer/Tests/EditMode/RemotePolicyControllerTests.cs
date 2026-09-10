using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Rl;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// experiment-plan step D — the learned-policy stabilizer as a bridge client.
    /// It must speak the wire format, turn the returned action into a rate
    /// command, and degrade to a locked Hold when the policy process is absent.
    /// </summary>
    public class RemotePolicyControllerTests
    {
        private static SensorBundle Bundle(double t) => new SensorBundle
        {
            StampSeconds = t,
            LoadCells = new LoadCellSample { Forces = new double[4] },
        };

        [Test]
        public void ConnectsAndTurnsTheActionIntoARateCommand()
        {
            const int stack = 6;
            int obsSize = ObservationEncoder.SingleFrameSize * stack;
            using var server = new FakeContractServer(obsSize, new[] { 0.5f, -0.25f });

            var ctl = new RemotePolicyController("127.0.0.1", server.Port, stack, rateScaleRadPerSec: 3.5);
            ctl.Reset();
            Assert.IsTrue(ctl.Connected, "should have connected to the fake server");
            StringAssert.Contains("@", ctl.Name);

            var cmd = ctl.ComputeCommand(Bundle(0.01));
            Assert.AreEqual(StabilizerCommandMode.JointRate, cmd.Mode);
            Assert.AreEqual(0.5 * 3.5, cmd.PitchCommand, 1e-4);
            Assert.AreEqual(-0.25 * 3.5, cmd.RollCommand, 1e-4);

            for (int i = 0; i < 20; i++) ctl.ComputeCommand(Bundle(0.02 + i * 0.01));
            Assert.GreaterOrEqual(server.InferCount, 21);
            Assert.AreEqual("policy-ok", ctl.ExecutionStatus);
            Assert.AreEqual(0, ctl.FallbackTicks);
            ctl.Dispose();
        }

        [Test]
        public void DegradesToLockedHold_WhenNoServerIsListening()
        {
            // nothing listening on this port
            var ctl = new RemotePolicyController("127.0.0.1", 59_999, 6, rateScaleRadPerSec: 3.5, timeoutMs: 300);
            ctl.Reset();
            Assert.IsFalse(ctl.Connected);
            StringAssert.Contains("offline", ctl.Name);

            var cmd = ctl.ComputeCommand(Bundle(0.01));
            Assert.AreEqual(StabilizerCommandMode.Locked, cmd.Mode, "no policy → the locked-Hold floor");
            Assert.DoesNotThrow(() => ctl.ComputeCommand(Bundle(0.02)));
            Assert.AreEqual("hold-fallback", ctl.ExecutionStatus);
            Assert.AreEqual(2, ctl.FallbackTicks);
            Assert.IsNotEmpty(ctl.FallbackReason);
            ctl.Dispose();
        }

        [Test]
        public void MidEpisodeDisconnect_IsStickyMixedExecution()
        {
            using var server = new FakeContractServer(ObservationEncoder.SingleFrameSize * 6, new[] { 0.2f, 0f }, disconnectAfter: 1);
            using var ctl = new RemotePolicyController("127.0.0.1", server.Port, timeoutMs: 100);
            ctl.Reset();
            ctl.ComputeCommand(Bundle(0.01));
            ctl.ComputeCommand(Bundle(0.02));
            Assert.AreEqual(1, ctl.PolicyTicks);
            Assert.AreEqual(1, ctl.FallbackTicks);
            Assert.AreEqual("mixed-policy-and-hold", ctl.ExecutionStatus);
            StringAssert.Contains(new string('a', 64), ctl.PolicyIdentity);
        }

        [Test]
        public void LegacyServer_RemainsUsableButPolicyIdentityIsUnverified()
        {
            using var server = new FakeContractServer(ObservationEncoder.SingleFrameSize * 6, new[] { 0f, 0f }, legacy: true);
            using var ctl = new RemotePolicyController("127.0.0.1", server.Port);
            ctl.Reset(); ctl.ComputeCommand(Bundle(0.01));
            Assert.AreEqual("policy-unverified", ctl.ExecutionStatus);
            Assert.AreEqual(0, ctl.FallbackTicks);
        }

        [Test]
        public void NonFiniteAction_FallsBackAndIsNotAnAiResult()
        {
            using var server = new FakeContractServer(ObservationEncoder.SingleFrameSize * 6, new[] { float.NaN, 0f });
            using var ctl = new RemotePolicyController("127.0.0.1", server.Port);
            ctl.Reset();
            Assert.AreEqual(StabilizerCommandMode.Locked, ctl.ComputeCommand(Bundle(0.01)).Mode);
            Assert.AreEqual("hold-fallback", ctl.ExecutionStatus);
        }

        [Test]
        public void RateCommandIsClampedToUnitActionRange()
        {
            const int stack = 6;
            int obsSize = ObservationEncoder.SingleFrameSize * stack;
            using var server = new FakeContractServer(obsSize, new[] { 9.0f, -9.0f }); // absurd

            var ctl = new RemotePolicyController("127.0.0.1", server.Port, stack, rateScaleRadPerSec: 3.5);
            ctl.Reset();
            var cmd = ctl.ComputeCommand(Bundle(0.01));
            Assert.AreEqual(3.5, cmd.PitchCommand, 1e-4);
            Assert.AreEqual(-3.5, cmd.RollCommand, 1e-4);
            ctl.Dispose();
        }
    }
}
