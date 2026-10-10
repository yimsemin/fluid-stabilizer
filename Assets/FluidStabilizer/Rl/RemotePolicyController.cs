using System;
using System.Net.Sockets;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;

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
        private readonly GimbalConfig _gimbal;    // the actuator this bridge drives
        private readonly int _timeoutMs;
        private readonly Action<string> _log;

        private ObservationEncoder _encoder;
        private PolicyContract _contract;
        private ActionComposer _composer;   // null = legacy plain absolute-rate path
        private TcpClient _client;
        private NetworkStream _stream;
        private int _obsSize, _actSize;
        private double _lastAp, _lastAr;
        private long _seq;
        private bool _connected, _warned;
        private long _retryAtSeq;   // while dropped, re-probe the server every ~2 s of control ticks
        private int _retriesLeft = 3;

        /// <param name="gimbal">
        /// The actuator being driven. Required to compose a <b>residual</b> policy's
        /// action the same way training did (baseline + residual, lock gate); when
        /// null only the legacy plain absolute-rate contract can be served.</param>
        public RemotePolicyController(
            string host = "127.0.0.1", int port = 5601, int frameStack = 6,
            double rateScaleRadPerSec = 3.5, int timeoutMs = 5000, Action<string> log = null,
            GimbalConfig gimbal = null)
        {
            _gimbal = gimbal ?? new GimbalConfig { MaxRateRadPerSec = rateScaleRadPerSec };
            _host = host;
            _port = port;
            _frameStack = Math.Max(1, frameStack);
            _rateScale = rateScaleRadPerSec;
            _timeoutMs = timeoutMs;
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// Called with every observation exactly as it is sent to the policy (and the
        /// sensors it came from) — DAgger relabels the learner's own visited states
        /// with the teacher's action (<c>DaggerRolloutRunner</c>). Null in normal use.
        /// </summary>
        public Action<float[], SensorBundle> ObservationTap { get; set; }

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
        /// <summary>The contract this bridge is actually executing under.</summary>
        public PolicyContract Contract => _contract;

        public string PolicySettings =>
            $"endpoint={_host}:{_port};stack={_frameStack};" +
            (_composer != null ? _contract.Encode() : "action=absolute-joint-rate;contract=legacy-assumed");

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
            _contract = PolicyContract.Legacy(_rateScale, _frameStack);
            _composer = null;
            _lastAp = _lastAr = 0.0;
            _seq = 0;
            _retriesLeft = 3;
            _retryAtSeq = 0;

            TryConnect();
            _composer?.Reset();

        }

        public StabilizerCommand ComputeCommand(in SensorBundle sensors)
        {
            // The lock state is part of the observation (findings F9): a policy that
            // chooses its own lock has to know which side of the switch it is on.
            float[] obs = _encoder.Observe(sensors, _lastAp, _lastAr, _composer?.IsLocked ?? false);
            ObservationTap?.Invoke(obs, sensors);

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
                    double lockReq = _actSize > 2 ? Clamp1(act[2]) : 0.0;

                    if (_composer != null)
                    {
                        // Same composition the policy was trained under — baseline,
                        // residual scale and lock decision all from its own contract.
                        _composer.SetAction(_lastAp, _lastAr, lockReq);
                        _seq++;
                        return _composer.ComputeCommand(sensors);
                    }
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

                if (_obsSize != ObservationEncoder.SingleFrameSize * _frameStack ||
                    (_actSize != 2 && _actSize != 3))
                    throw new InvalidOperationException("policy observation/action shape mismatch");
                string identity = "unknown (legacy protocol v1)";
                if (version == 2)
                {
                    byte[] meta = BridgeIo.ReadFrame(_stream);
                    if (meta == null) throw new InvalidOperationException("missing policy identity");
                    identity = System.Text.Encoding.UTF8.GetString(meta);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(identity, @"^checkpoint-sha256=[0-9a-f]{64};"))
                        throw new InvalidOperationException("invalid policy identity");

                    // A v2 server may state the contract its checkpoint was trained
                    // under. Honour it exactly, or refuse — running a residual policy
                    // as if it were an absolute-rate one is the failure this guards.
                    int ci = identity.IndexOf("contract=", StringComparison.Ordinal);
                    if (ci >= 0)
                    {
                        string raw = identity.Substring(ci + "contract=".Length);
                        if (!PolicyContract.TryParse(raw, out PolicyContract c))
                            throw new InvalidOperationException("unparseable policy contract: " + raw);
                        if (c.FrameStack != _frameStack)
                            throw new InvalidOperationException(
                                $"policy frame stack {c.FrameStack} != bridge {_frameStack}");
                        if (c.ActionSize != _actSize)
                            throw new InvalidOperationException(
                                $"policy contract action size {c.ActionSize} != server {_actSize}");
                        _composer = ActionComposerFactory.From(c, _gimbal);
                        _composer.Reset();
                        _contract = c;
                        _log("[policy] contract " + c.Encode());
                    }
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
