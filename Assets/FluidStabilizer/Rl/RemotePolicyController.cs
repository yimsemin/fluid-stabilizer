using System;
using System.Net.Sockets;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Rl
{
    /// <summary>
    /// The ③ "learned policy" stabilizer as a bridge <b>client</b>: it encodes the
    /// observation locally (same <see cref="ObservationEncoder"/> as training),
    /// sends the vector to a policy-inference process (<c>python/serve_policy.py</c>)
    /// over TCP, and turns the returned action into a joint-rate command.
    ///
    /// This keeps the deployment split a real robot has — plant here, brain in its
    /// own process, linked by the ROS-2-stand-in bridge (project-goals.md,
    /// design-review §12). Engine-free: no <c>UnityEngine</c> reference, so it runs
    /// unchanged headless, in the play scene, or against a ROS 2 node later.
    ///
    /// If the server is unreachable the controller degrades to a locked
    /// <see cref="StabilizerCommand.Hold"/> (and logs once) so a comparison run
    /// never hard-fails on a missing policy process.
    ///
    /// <para><b>Wire format</b> (little-endian, <c>[u32 len][payload]</c> frames):
    /// on connect the server sends <c>[u32 magic=0x504F4C59][u32 version=1]
    /// [i32 obsSize][i32 actSize]</c>. Then per tick the client sends
    /// <c>[u8 op=1][f32 obs × obsSize]</c> and reads <c>[f32 act × actSize]</c>.
    /// <c>op=0</c> = reset (clears any server-side episode state), <c>op=2</c> = close.</para>
    /// </summary>
    public sealed class RemotePolicyController : IController, IDisposable, IPolicyExecutionStatus
    {
        public const uint Magic = 0x504F4C59; // "POLY"
        public const uint Version = 2;

        private const byte OpReset = 0;
        private const byte OpInfer = 1;
        private const byte OpClose = 2;

        private readonly string _host;
        private readonly int _port;
        private readonly int _frameStack;
        private readonly double _rateScale;
        private readonly int _timeoutMs;
        private readonly Action<string> _log;

        private ObservationEncoder _encoder;
        private TcpClient _client;
        private NetworkStream _stream;
        private int _obsSize, _actSize;
        private double _lastAp, _lastAr;
        private long _seq;
        private bool _connected, _warned;
        private long _retryAtSeq;   // while dropped, re-probe the server every ~2 s of control ticks
        private int _retriesLeft = 3;

        public RemotePolicyController(
            string host = "127.0.0.1", int port = 5601, int frameStack = 6,
            double rateScaleRadPerSec = 3.5, int timeoutMs = 5000, Action<string> log = null)
        {
            _host = host;
            _port = port;
            _frameStack = Math.Max(1, frameStack);
            _rateScale = rateScaleRadPerSec;
            _timeoutMs = timeoutMs;
            _log = log ?? (_ => { });
        }

        public bool Connected => _connected;
        public string PolicyIdentity { get; private set; } = "unknown";
        public long PolicyTicks { get; private set; }
        public long FallbackTicks { get; private set; }
        public string FallbackReason { get; private set; } = "";
        private bool _identityVerified, _identityChanged;
        public string ExecutionStatus => FallbackTicks > 0
            ? (PolicyTicks > 0 ? "mixed-policy-and-hold" : "hold-fallback")
            : PolicyTicks == 0 ? "not-executed"
            : !_identityVerified || _identityChanged ? "policy-unverified" : "policy-ok";
        public string PolicySettings => $"endpoint={_host}:{_port};stack={_frameStack};rateScale={_rateScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};action=absolute-joint-rate";

        private StabilizerCommand Fallback(in SensorBundle sensors)
        {
            FallbackTicks++;
            _lastAp = _lastAr = 0;
            return StabilizerCommand.Hold(_seq++, sensors.StampSeconds);
        }

        public string Name => _connected
            ? $"Learned policy @{_host}:{_port}"
            : $"Learned policy ({_host}:{_port} offline → Hold)";

        public void Reset()
        {
            PolicyTicks = FallbackTicks = 0;
            FallbackReason = "";
            PolicyIdentity = "unknown";
            _identityVerified = _identityChanged = false;
            _warned = false;
            _encoder = new ObservationEncoder(_frameStack);
            _lastAp = _lastAr = 0.0;
            _seq = 0;
            _retriesLeft = 3;
            _retryAtSeq = 0;

            TryConnect();

        }

        public StabilizerCommand ComputeCommand(in SensorBundle sensors)
        {
            float[] obs = _encoder.Observe(sensors, _lastAp, _lastAr);

            if (!_connected)
            {
                if (_seq >= _retryAtSeq && _retriesLeft > 0)
                {
                    _retriesLeft--;
                    TryConnect();
                    _retryAtSeq = _seq + 200;
                }
                if (!_connected) return Fallback(sensors);
            }

            try
            {
                if (obs.Length != _obsSize)
                    throw new InvalidOperationException(
                        $"obs size {obs.Length} != server's {_obsSize} (frame-stack mismatch?)");

                BridgeIo.WriteFrame(_stream, BridgeIo.PackFloats(obs, OpInfer));
                byte[] resp = BridgeIo.ReadFrame(_stream);
                if (resp == null || resp.Length != _actSize * 4) { Drop("infer: short/closed response"); }
                else
                {
                    float[] act = BridgeIo.UnpackFloats(resp, 0, _actSize);
                    if (Array.Exists(act, x => float.IsNaN(x) || float.IsInfinity(x)))
                        throw new InvalidOperationException("non-finite policy action");
                    PolicyTicks++;
                    _lastAp = Clamp1(act[0]);
                    _lastAr = _actSize > 1 ? Clamp1(act[1]) : 0.0;
                    return StabilizerCommand.Rate(_seq++, sensors.StampSeconds,
                        _lastAp * _rateScale, _lastAr * _rateScale);
                }
            }
            catch (Exception e) { Drop("infer: " + e.Message); }

            return Fallback(sensors);
        }

        private void TryConnect()
        {
            Cleanup();
            try
            {
                _client = new TcpClient { NoDelay = true };
                var ar = _client.BeginConnect(_host, _port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(_timeoutMs) || !_client.Connected)
                    throw new SocketException((int)SocketError.TimedOut);
                _client.EndConnect(ar);
                _client.Client.Blocking = true; // Mono can leave an async-connected socket non-blocking

                _stream = _client.GetStream();
                _stream.ReadTimeout = _timeoutMs;
                _stream.WriteTimeout = _timeoutMs;

                var hs = new byte[16];
                if (!BridgeIo.ReadExact(_stream, hs, 16)) throw new InvalidOperationException("no handshake");
                uint magic = BridgeIo.GetU32(hs, 0);
                uint version = BridgeIo.GetU32(hs, 4);
                if (magic != Magic || (version != 1 && version != Version))
                    throw new InvalidOperationException($"bad handshake magic={magic:X} version={version}");
                _obsSize = BridgeIo.GetI32(hs, 8);
                _actSize = BridgeIo.GetI32(hs, 12);

                if (_obsSize != ObservationEncoder.SingleFrameSize * _frameStack || _actSize != 2)
                    throw new InvalidOperationException("policy observation/action shape mismatch");
                string identity = "unknown (legacy protocol v1)";
                if (version == 2)
                {
                    byte[] meta = BridgeIo.ReadFrame(_stream);
                    if (meta == null) throw new InvalidOperationException("missing policy identity");
                    identity = System.Text.Encoding.UTF8.GetString(meta);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(identity, @"^checkpoint-sha256=[0-9a-f]{64};"))
                        throw new InvalidOperationException("invalid policy identity");
                }
                if (PolicyIdentity != "unknown" && PolicyIdentity != identity) _identityChanged = true;
                if (PolicyIdentity == "unknown") PolicyIdentity = identity;
                else if (!PolicyIdentity.Contains(identity)) PolicyIdentity += " | " + identity;
                _identityVerified = version == 2;
                BridgeIo.WriteFrame(_stream, new[] { OpReset });
                byte[] ack = BridgeIo.ReadFrame(_stream);
                if (ack == null || ack.Length != 1 || ack[0] != 1)
                    throw new InvalidOperationException("invalid reset acknowledgement");
                _connected = true;
                _log($"[policy] connected to {_host}:{_port} (obs={_obsSize} act={_actSize})");
            }
            catch (Exception e)
            {
                _connected = false;
                FallbackReason = "connect/reset: " + e.Message;
                if (!_warned) { _warned = true; _log($"[policy] {_host}:{_port} unavailable ({e.Message}) — using locked Hold."); }
                Cleanup();
            }
        }

        private void Drop(string why)
        {
            _connected = false;
            FallbackReason = why;
            _retryAtSeq = _seq + 200;
            if (!_warned) { _warned = true; _log($"[policy] connection lost ({why}) — using locked Hold."); }
            Cleanup();
        }

        private void Cleanup()
        {
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        public void Dispose()
        {
            if (_connected && _stream != null)
                try { BridgeIo.WriteFrame(_stream, new[] { OpClose }); } catch { }
            Cleanup();
            _connected = false;
        }

        private static double Clamp1(double v) => v < -1.0 ? -1.0 : (v > 1.0 ? 1.0 : v);
    }
}
