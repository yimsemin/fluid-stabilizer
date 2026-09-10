using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Rl;

namespace FluidStabilizer.Tests
{
    public class TrainingServerTests
    {
        private static RlEnvironment MakeEnv() => new RlEnvironment(
            new SimulationConfig { EpisodeSeconds = 8.0 },
            seed => new HighwayDriver(seed, briskness: 0.6),
            () => new PendulumSloshModel(),
            RewardWeights.Default(),
            frameStackLength: 6);

        [Test]
        public void Socket_Handshake_Reset_Step_MatchesTheInProcessEnvironment()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverThread = new Thread(() =>
            {
                using TcpClient c = listener.AcceptTcpClient();
                try { new TrainingServer(MakeEnv).ServeClient(c); }
                catch (IOException) { }
                catch (SocketException) { }
            }) { IsBackground = true };
            serverThread.Start();

            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                client.NoDelay = true;
                var s = client.GetStream();

                // handshake
                byte[] hs = ReadExactly(s, 20);
                Assert.AreEqual(TrainingServer.Magic, BitConverter.ToUInt32(hs, 0), "bad magic");
                Assert.AreEqual(TrainingServer.Version, BitConverter.ToUInt32(hs, 4));
                int obsSize = BitConverter.ToInt32(hs, 8);
                int privSize = BitConverter.ToInt32(hs, 12);
                int actSize = BitConverter.ToInt32(hs, 16);

                // v2 states the action contract before any episode traffic.
                string contract = System.Text.Encoding.UTF8.GetString(ReadFrame(s));
                Assert.IsTrue(PolicyContract.TryParse(contract, out PolicyContract c),
                    "handshake must carry a parseable action contract: " + contract);
                Assert.AreEqual(PolicyContract.CurrentObsVersion, c.ObsVersion);

                var reference = MakeEnv();
                Assert.AreEqual(reference.ObservationSize, obsSize);
                Assert.AreEqual(reference.PrivilegedObservationSize, privSize);
                Assert.AreEqual(2, actSize);

                // RESET(7)
                WriteFrame(s, Concat(new byte[] { 0 }, BitConverter.GetBytes((ulong)7)));
                float[] obs = Floats(ReadFrame(s), obsSize);
                float[] refObs = reference.Reset(7);
                CollectionAssert.AreEqual(refObs, obs, "reset observation differs from the in-process env");

                // a few deterministic steps
                (float, float)[] acts = { (0.2f, -0.1f), (-0.5f, 0.3f), (0.0f, 0.0f), (0.9f, -0.9f) };
                foreach (var (a0, a1) in acts)
                {
                    var body = new byte[9];
                    body[0] = 1;
                    Array.Copy(BitConverter.GetBytes(a0), 0, body, 1, 4);
                    Array.Copy(BitConverter.GetBytes(a1), 0, body, 5, 4);
                    WriteFrame(s, body);

                    byte[] resp = ReadFrame(s);
                    int o = 0;
                    float[] stepObs = new float[obsSize];
                    for (int i = 0; i < obsSize; i++) { stepObs[i] = BitConverter.ToSingle(resp, o); o += 4; }
                    float reward = BitConverter.ToSingle(resp, o); o += 4;
                    bool terminated = resp[o++] != 0;
                    bool truncated = resp[o++] != 0;
                    float[] priv = new float[privSize];
                    for (int i = 0; i < privSize; i++) { priv[i] = BitConverter.ToSingle(resp, o); o += 4; }
                    Assert.AreEqual(resp.Length, o, "response framing size mismatch");

                    var r = reference.Step(a0, a1);
                    CollectionAssert.AreEqual(r.Observation, stepObs, "step observation differs");
                    Assert.AreEqual((float)r.Reward, reward, 0f, "reward differs");
                    Assert.AreEqual(r.Terminated, terminated);
                    Assert.AreEqual(r.Truncated, truncated);
                    CollectionAssert.AreEqual(r.PrivilegedObservation, priv, "privileged observation differs");
                }

                WriteFrame(s, new byte[] { 2 }); // CLOSE
            }

            serverThread.Join(2000);
            listener.Stop();
        }

        // ---- framing helpers (client side) ----

        private static void WriteFrame(Stream s, byte[] payload)
        {
            s.Write(BitConverter.GetBytes((uint)payload.Length), 0, 4);
            s.Write(payload, 0, payload.Length);
        }

        private static byte[] ReadFrame(Stream s)
        {
            byte[] len = ReadExactly(s, 4);
            return ReadExactly(s, (int)BitConverter.ToUInt32(len, 0));
        }

        private static byte[] ReadExactly(Stream s, int n)
        {
            var b = new byte[n];
            int off = 0;
            while (off < n)
            {
                int k = s.Read(b, off, n - off);
                if (k <= 0) throw new EndOfStreamException();
                off += k;
            }
            return b;
        }

        private static float[] Floats(byte[] b, int n)
        {
            var f = new float[n];
            for (int i = 0; i < n; i++) f[i] = BitConverter.ToSingle(b, i * 4);
            return f;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Array.Copy(a, r, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }
}
