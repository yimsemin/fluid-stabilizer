using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FluidStabilizer.Rl;

namespace FluidStabilizer.Tests
{
    /// <summary>A minimal stand-in for python/serve_policy.py: handshake, then reply
    /// a fixed action to every INFER. Runs on a background thread for one client.</summary>
    public sealed class FakeContractServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly float[] _reply;
        private readonly string _contract;
        public int Port { get; }
        public volatile int InferCount;

        public FakeContractServer(int obsSize, float[] reply, int disconnectAfter = int.MaxValue,
            bool legacy = false, string contract = null)
        {
            _contract = contract;
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
                    Le(hs, 4, legacy ? 1u : RemotePolicyController.Version);
                    Le(hs, 8, obsSize);
                    Le(hs, 12, reply.Length);
                    s.Write(hs, 0, 16);
                    if (!legacy)
                        WriteFrame(s, System.Text.Encoding.UTF8.GetBytes(
                            "checkpoint-sha256=" + new string('a', 64) + ";" +
                            (_contract != null ? "contract=" + _contract
                                               : "action=absolute-joint-rate")));

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
                            if (InferCount >= disconnectAfter) return;
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
}
