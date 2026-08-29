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
        /// <summary>A minimal stand-in for python/serve_policy.py: handshake, then reply
        /// a fixed action to every INFER. Runs on a background thread for one client.</summary>
        private sealed class FakePolicyServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _thread;
            private readonly float[] _reply;
            public int Port { get; }
            public volatile int InferCount;

            public FakePolicyServer(int obsSize, float[] reply)
            {
                _reply = reply;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

                _thread = new Thread(() =>
                {
                    try
                    {
                        using TcpClient c = _listener.AcceptTcpClient();
                        c.NoDelay = true;
                        NetworkStream s = c.GetStream();

                        var hs = new byte[16];
                        Le(hs, 0, RemotePolicyController.Magic);
                        Le(hs, 4, RemotePolicyController.Version);
                        Le(hs, 8, obsSize);
                        Le(hs, 12, reply.Length);
                        s.Write(hs, 0, 16);

                        while (true)
                        {
                            var lb = new byte[4];
                            if (!ReadExact(s, lb, 4)) return;
                            int len = BitConverter.ToInt32(lb, 0);
                            var body = new byte[len];
                            if (len > 0 && !ReadExact(s, body, len)) return;
                            byte op = body[0];
                            if (op == 2) return;
                            if (op == 0) { WriteFrame(s, new byte[] { 1 }); continue; }
                            if (op == 1)
                            {
                                InferCount++;
                                var payload = new byte[reply.Length * 4];
                                for (int i = 0; i < reply.Length; i++)
                                    Array.Copy(BitConverter.GetBytes(reply[i]), 0, payload, i * 4, 4);
                                WriteFrame(s, payload);
                            }
                        }
                    }
                    catch { /* test teardown */ }
                })
                { IsBackground = true };
                _thread.Start();
            }

            private static void Le(byte[] b, int o, uint v) => Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4);
            private static void Le(byte[] b, int o, int v) => Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4);

            private static bool ReadExact(NetworkStream s, byte[] buf, int n)
            {
                int off = 0;
                while (off < n)
                {
                    int r = s.Read(buf, off, n - off);
                    if (r <= 0) return false;
                    off += r;
                }
                return true;
            }

            private static void WriteFrame(NetworkStream s, byte[] payload)
            {
                s.Write(BitConverter.GetBytes(payload.Length), 0, 4);
                s.Write(payload, 0, payload.Length);
                s.Flush();
            }

            public void Dispose()
            {
                try { _listener.Stop(); } catch { }
            }
        }

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
            using var server = new FakePolicyServer(obsSize, new[] { 0.5f, -0.25f });

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
            ctl.Dispose();
        }

        [Test]
        public void RateCommandIsClampedToUnitActionRange()
        {
            const int stack = 6;
            int obsSize = ObservationEncoder.SingleFrameSize * stack;
            using var server = new FakePolicyServer(obsSize, new[] { 9.0f, -9.0f }); // absurd

            var ctl = new RemotePolicyController("127.0.0.1", server.Port, stack, rateScaleRadPerSec: 3.5);
            ctl.Reset();
            var cmd = ctl.ComputeCommand(Bundle(0.01));
            Assert.AreEqual(3.5, cmd.PitchCommand, 1e-4);
            Assert.AreEqual(-3.5, cmd.RollCommand, 1e-4);
            ctl.Dispose();
        }
    }
}
