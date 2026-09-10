using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace FluidStabilizer.Rl
{
    /// <summary>
    /// A tiny length-prefixed binary TCP bridge that exposes one
    /// <see cref="RlEnvironment"/> to an external trainer (design-review §12: a
    /// language-agnostic Gymnasium-style boundary, not Unity ML-Agents). One
    /// client at a time; the sim is single-threaded and deterministic.
    ///
    /// <para><b>Wire format</b> (all little-endian):</para>
    /// On connect the server sends a 20-byte handshake:
    /// <c>magic u32 = 0x464C5544 ("DULF" bytes), version u32 = 1,
    /// obsSize i32, privSize i32, actSize i32</c>.
    /// Then request/response frames, each <c>[u32 length][payload]</c>:
    /// <list type="bullet">
    /// <item>RESET request <c>[u8 op=0][u64 seed]</c> → response <c>[f32 obs × obsSize]</c></item>
    /// <item>STEP request <c>[u8 op=1][f32 action × actSize]</c> → response
    ///   <c>[f32 obs][f32 reward][u8 terminated][u8 truncated][f32 priv × privSize]</c></item>
    /// <item>CLOSE request <c>[u8 op=2]</c> → connection closes</item>
    /// </list>
    /// </summary>
    public sealed class TrainingServer
    {
        public const uint Magic = 0x464C5544;
        public const uint Version = 2;

        private const byte OpReset = 0;
        private const byte OpStep = 1;
        private const byte OpClose = 2;

        private readonly Func<RlEnvironment> _envFactory;

        public TrainingServer(Func<RlEnvironment> envFactory)
        {
            _envFactory = envFactory ?? throw new ArgumentNullException(nameof(envFactory));
        }

        public int Port { get; private set; }

        /// <summary>Bind and block, serving clients one after another until interrupted.</summary>
        public void Serve(int port = 5555, string host = "127.0.0.1")
        {
            var listener = new TcpListener(IPAddress.Parse(host), port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Console.WriteLine($"[rl] TrainingServer listening on {host}:{Port}");
            try
            {
                while (true)
                {
                    using TcpClient client = listener.AcceptTcpClient();
                    Console.WriteLine("[rl] client connected");
                    try { ServeClient(client); }
                    catch (IOException) { }
                    catch (SocketException) { }
                    Console.WriteLine("[rl] client disconnected");
                }
            }
            finally { listener.Stop(); }
        }

        /// <summary>Serve a single already-accepted client until it closes. Public for tests.</summary>
        public void ServeClient(TcpClient client)
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            RlEnvironment env = _envFactory();

            var hs = new byte[20];
            Put(hs, 0, Magic);
            Put(hs, 4, Version);
            Put(hs, 8, env.ObservationSize);
            Put(hs, 12, env.PrivilegedObservationSize);
            Put(hs, 16, env.ActionSize);
            stream.Write(hs, 0, hs.Length);

            // v2: the action contract this environment trains under, so the trainer
            // can stamp it into the checkpoint and the deployed bridge can execute
            // the policy the way it was trained (PolicyContract).
            WriteFrame(stream, System.Text.Encoding.UTF8.GetBytes(env.Contract.Encode()));

            while (true)
            {
                byte[] req = ReadFrame(stream);
                if (req == null || req.Length == 0) return;

                switch (req[0])
                {
                    case OpClose:
                        return;

                    case OpReset:
                    {
                        ulong seed = GetU64(req, 1);
                        float[] obs = env.Reset(seed);
                        WriteFrame(stream, PackFloats(obs));
                        break;
                    }

                    case OpStep:
                    {
                        float a0 = GetF32(req, 1);
                        float a1 = GetF32(req, 5);
                        RlStepResult r = env.Step(a0, a1);
                        WriteFrame(stream, PackStep(r));
                        break;
                    }

                    default:
                        throw new InvalidDataException($"unknown opcode {req[0]}");
                }
            }
        }

        // ---- framing ----

        private static byte[] ReadFrame(Stream s)
        {
            var lenBuf = new byte[4];
            if (!ReadExact(s, lenBuf, 4)) return null;
            int len = (int)GetU32(lenBuf, 0);
            if (len < 0 || len > 64 * 1024 * 1024) throw new InvalidDataException($"frame length {len}");
            var buf = new byte[len];
            if (len > 0 && !ReadExact(s, buf, len)) return null;
            return buf;
        }

        private static void WriteFrame(Stream s, byte[] payload)
        {
            var len = new byte[4];
            Put(len, 0, (uint)payload.Length);
            s.Write(len, 0, 4);
            s.Write(payload, 0, payload.Length);
        }

        private static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        private static byte[] PackFloats(float[] v)
        {
            var b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) Put(b, i * 4, v[i]);
            return b;
        }

        private static byte[] PackStep(in RlStepResult r)
        {
            int n = r.Observation.Length;
            int m = r.PrivilegedObservation.Length;
            var b = new byte[n * 4 + 4 + 1 + 1 + m * 4];
            int o = 0;
            for (int i = 0; i < n; i++) { Put(b, o, r.Observation[i]); o += 4; }
            Put(b, o, (float)r.Reward); o += 4;
            b[o++] = (byte)(r.Terminated ? 1 : 0);
            b[o++] = (byte)(r.Truncated ? 1 : 0);
            for (int i = 0; i < m; i++) { Put(b, o, r.PrivilegedObservation[i]); o += 4; }
            return b;
        }

        // ---- little-endian scalar IO (Apple Silicon / x86 are LE; guard anyway) ----

        private static void Put(byte[] b, int o, uint v) => Write(b, o, BitConverter.GetBytes(v));
        private static void Put(byte[] b, int o, int v) => Write(b, o, BitConverter.GetBytes(v));
        private static void Put(byte[] b, int o, float v) => Write(b, o, BitConverter.GetBytes(v));

        private static void Write(byte[] b, int o, byte[] src)
        {
            if (!BitConverter.IsLittleEndian) Array.Reverse(src);
            Array.Copy(src, 0, b, o, src.Length);
        }

        private static uint GetU32(byte[] b, int o) => (uint)Read(b, o, 4, BitConverter.ToUInt32);
        private static ulong GetU64(byte[] b, int o)
        {
            var t = Slice(b, o, 8);
            return BitConverter.ToUInt64(t, 0);
        }
        private static float GetF32(byte[] b, int o)
        {
            var t = Slice(b, o, 4);
            return BitConverter.ToSingle(t, 0);
        }

        private static long Read(byte[] b, int o, int n, Func<byte[], int, uint> f) => f(Slice(b, o, n), 0);

        private static byte[] Slice(byte[] b, int o, int n)
        {
            var t = new byte[n];
            Array.Copy(b, o, t, 0, n);
            if (!BitConverter.IsLittleEndian) Array.Reverse(t);
            return t;
        }
    }
}
