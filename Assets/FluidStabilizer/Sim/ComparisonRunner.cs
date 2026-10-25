using System;
using System.Collections.Generic;
using System.Linq;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Rl;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// The headline comparison (experiment-plan.md step 2): replays one recorded
    /// drive through several stabilizers <em>at once</em>, in a <b>split screen</b>
    /// — one horizontal strip per stabilizer, each its own camera on its own copy
    /// of the identical road (same seed). So it reads as "the same drive, run
    /// three ways" and the 4-lane road position never confuses the picture.
    /// Every lane is its own deterministic <see cref="SimulationLoop"/> fed the
    /// same <see cref="DriverTrace"/>; a per-strip panel shows litres spilled,
    /// time near the rim, and peak slosh energy, best strip highlighted.
    ///
    /// Toggle with <c>C</c> in the play scene (uses the newest recorded trace).
    /// While active the normal single-sim runner is paused.
    /// </summary>
    public sealed class ComparisonRunner : MonoBehaviour
    {
        public SimulationRunner Runner;      // paused while a comparison runs
        public Transform RigTemplate;        // the SceneBuilder TruckRig — cloned per lane
        public Camera Camera;                // the main play camera — disabled while a comparison runs

        [Tooltip("One strip per entry — the headline ①②③. The learned lane needs python/serve_policy.py; " +
                 "if it is not running that lane shows '(offline)' and falls back to a locked Hold.")]
        public StabilizerChoice[] Lanes =
        {
            StabilizerChoice.Off,                        // ① rigid tank
            StabilizerChoice.SpecificForceAlignmentPlusI, // ② rule-based (PID)
            StabilizerChoice.LearnedPolicy,              // ③ learned policy (serve_policy.py)
        };

        // Each lane lives in its own far-apart slice of the world so its camera
        // never catches a neighbour. Physics is indexed by distance travelled, so
        // the offset is purely visual.
        private const float LaneWorldGap = 600f;

        [Header("Learned-policy lane (python/serve_policy.py)")]
        public string PolicyHost = "127.0.0.1";
        public int PolicyPort = 5601;
        public int PolicyFrameStack = 6;
        public int PolicyConnectTimeoutMs = 2000;

        /// <summary>Port for <see cref="StabilizerChoice.LearnedPolicyB"/> — the "새 정책" lane
        /// in <see cref="StartOldVsNewComparison"/>, a second <c>serve_policy.py</c> process
        /// serving a different checkpoint than the one on <see cref="PolicyPort"/>.</summary>
        public int PolicyPortB = 5602;

        /// <summary>Per-lane title override for the current run (set by
        /// <see cref="StartOldVsNewComparison"/>); null = the usual "①②③" + ShortName.</summary>
        private string[] _laneLabels;

        /// <summary>Freeboard (m) below which a lane is "near the rim".</summary>
        public double NearRimMargin = 0.05;

        public bool Active { get; private set; }

        /// <summary>The first strip's camera (editor screenshot aid — the main camera is disabled while a comparison runs).</summary>
        public Camera FirstStripCamera => _lanes.Count > 0 ? _lanes[0].Cam : null;

        /// <summary>Editor capture aids: lane count, a lane's camera, and its spill so far [L].</summary>
        public int LaneCount => _lanes.Count;
        public Camera LaneCamera(int i) => _lanes[i].Cam;
        public double LaneSpillLitres(int i) => _lanes[i].SpillLitres;

        private sealed class Lane
        {
            public StabilizerChoice Choice;
            public IController Controller;
            public SimulationLoop Loop;
            public GameObject Rig;
            public SimView View;
            public RoadRibbon Ribbon;
            public Camera Cam;
            public Vector3 WorldOffset;
            public double NearRimSeconds;
            public double PeakSloshKE;
            public double TorqueSatSeconds;

            public double SpillLitres => Loop.LatestGroundTruth.CumulativeOverflowVolume * 1000.0;
        }

        private readonly List<Lane> _lanes = new List<Lane>();
        private DriverTrace _trace;
        private double _accumulator;
        private bool _finished;

        // ------------------------------------------------------------- lifecycle

        public void Toggle()
        {
            if (Active) Stop();
            else StartFromNewestTrace();
        }

        public void StartFromNewestTrace(StabilizerChoice[] lanesOverride = null, string[] labelsOverride = null)
        {
            string path = TraceStore.NewestFile();
            if (path == null)
            {
                Debug.LogWarning("[compare] no recorded traces in " + TraceStore.Dir + " — press P to record one first.");
                return;
            }
            StartComparison(DriverTrace.LoadFromFile(path), withRigs: true, lanesOverride, labelsOverride);
        }

        public void StartComparison(DriverTrace trace) => StartComparison(trace, withRigs: true);

        /// <summary>
        /// <paramref name="withRigs"/> = false skips all Unity scene objects — for headless tests.
        /// <paramref name="lanesOverride"/>/<paramref name="labelsOverride"/> run a one-off lane
        /// set (e.g. <see cref="StartOldVsNewComparison"/>) without touching <see cref="Lanes"/>,
        /// so the next plain <c>C</c> press still gets the usual ①②③.
        /// </summary>
        public void StartComparison(DriverTrace trace, bool withRigs, StabilizerChoice[] lanesOverride = null, string[] labelsOverride = null)
        {
            Stop();
            StabilizerChoice[] lanes = lanesOverride ?? Lanes;
            _laneLabels = labelsOverride;
            _trace = trace;
            if (!string.IsNullOrEmpty(trace.Assumptions)) Debug.LogWarning("[compare] legacy assumptions: " + trace.Assumptions);
            _finished = false;
            _accumulator = 0;

            if (withRigs)
            {
                if (Runner != null)
                {
                    Runner.enabled = false;
                    SetComponentEnabled<SimView>(Runner.gameObject, false);
                    SetComponentEnabled<RoadRibbon>(Runner.gameObject, false);
                    Runner.GetComponent<RoadRibbon>()?.SetStripVisible(false); // OnDisable isn't reliable outside play mode
                }
                if (RigTemplate != null) RigTemplate.gameObject.SetActive(false);
                if (Camera != null) Camera.enabled = false;   // the split-screen lane cameras take over
            }

            int n = lanes.Length;
            for (int i = 0; i < n; i++)
            {
                IController ctrl = BuildLaneController(lanes[i], trace);
                var loop = new SimulationLoop(
                    trace.ToConfig(), ctrl, new RecordedTraceDriver(trace), trace.MakeSloshModel());
                loop.BeginEpisode(trace.Seed);

                var lane = new Lane
                {
                    Choice = lanes[i], Controller = ctrl, Loop = loop,
                    WorldOffset = new Vector3(i * LaneWorldGap, 0f, 0f),
                };

                if (withRigs && RigTemplate != null)
                {
                    var rig = Instantiate(RigTemplate.gameObject);
                    rig.name = "CompareLane_" + lanes[i];
                    rig.SetActive(true);

                    var view = rig.AddComponent<SimView>();
                    view.TruckBody = rig.transform;
                    view.TankPivot = rig.transform.Find("TankPivot");
                    view.TankBody = view.TankPivot.Find("TankBody");
                    view.LiquidMesh = view.TankBody.Find("LiquidMesh").GetComponent<MeshFilter>();
                    view.LoopOverride = loop;
                    view.WorldOffset = lane.WorldOffset;
                    view.ExternallyDriven = true;
                    lane.Rig = rig;
                    lane.View = view;

                    // Each strip gets its own copy of the (identical) road.
                    var ribbon = rig.AddComponent<RoadRibbon>();
                    ribbon.LoopOverride = loop;
                    ribbon.WorldOffset = lane.WorldOffset;
                    ribbon.ExternallyDriven = true;
                    lane.Ribbon = ribbon;

                    // Its own camera, rendering a horizontal strip (① top → ③ bottom).
                    var camGo = new GameObject("CompareCam_" + lanes[i]);
                    var cam = camGo.AddComponent<Camera>();
                    if (Camera != null) cam.CopyFrom(Camera);
                    cam.enabled = true;
                    cam.targetTexture = null;
                    cam.depth = 10 + i;
                    cam.rect = new Rect(0f, 1f - (i + 1f) / n, 1f, 1f / n);
                    lane.Cam = cam;
                }

                _lanes.Add(lane);
            }

            Active = true;
            Debug.Log($"[compare] '{trace.Label}' — split screen, {_lanes.Count} strips: {string.Join(", ", lanes.Select(SimulationRunner.ShortName))}");
        }

        /// <summary>The learned lane(s) are bridge clients; the rest are the engine-free analytic controllers.</summary>
        private IController BuildLaneController(StabilizerChoice choice, DriverTrace trace)
        {
            if (choice != StabilizerChoice.LearnedPolicy && choice != StabilizerChoice.LearnedPolicyB)
                return SimulationRunner.MakeController(choice);

            string host = Runner != null ? Runner.PolicyHost : PolicyHost;
            int port = choice == StabilizerChoice.LearnedPolicyB
                ? PolicyPortB
                : (Runner != null ? Runner.PolicyPort : PolicyPort);
            int stack = Runner != null ? Runner.PolicyFrameStack : PolicyFrameStack;
            var gimbal = trace.ToConfig().Gimbal;
            return new RemotePolicyController(host, port, stack,
                gimbal.MaxRateRadPerSec, PolicyConnectTimeoutMs, s => Debug.Log(s), gimbal);
        }

        /// <summary>
        /// "청중 데이터로 재학습했더니 실제로 나아졌나?" — a 3-lane comparison of
        /// ① rigid (the project's one hard baseline, CLAUDE.md) against the
        /// policy that was already running (<see cref="PolicyPort"/>, left
        /// untouched) and the one <see cref="LiveTrainingPanel"/> just trained
        /// (a second <c>serve_policy.py</c> the caller has started on
        /// <paramref name="newPolicyPort"/>). Uses the newest recorded trace,
        /// like the normal <c>C</c> comparison. A later plain <c>C</c> press
        /// goes back to the usual ①②③ — this does not touch <see cref="Lanes"/>.
        /// </summary>
        public void StartOldVsNewComparison(int newPolicyPort)
        {
            PolicyPortB = newPolicyPort;
            StartFromNewestTrace(
                new[] { StabilizerChoice.Off, StabilizerChoice.LearnedPolicy, StabilizerChoice.LearnedPolicyB },
                new[] { "① 강체", "이전 정책", "새 정책" });
        }

        public void Stop()
        {
            foreach (var l in _lanes)
            {
                if (l.Controller is IDisposable d) { try { d.Dispose(); } catch { } }
                if (l.Cam != null) Destroy(l.Cam.gameObject);
                if (l.Rig != null) Destroy(l.Rig);
            }
            _lanes.Clear();

            if (!Active) return;
            Active = false;

            if (RigTemplate != null) RigTemplate.gameObject.SetActive(true);
            if (Camera != null) Camera.enabled = true;
            if (Runner != null)
            {
                Runner.enabled = true;
                SetComponentEnabled<SimView>(Runner.gameObject, true);
                SetComponentEnabled<RoadRibbon>(Runner.gameObject, true);
            }
            Debug.Log("[compare] stopped");
        }

        // ---------------------------------------------------------------- update

        private void Update()
        {
            if (!Active || _lanes.Count == 0 || _finished) return;
            float scale = Runner != null ? Mathf.Max(0.05f, Runner.TimeScale) : 1f;
            StepLanes(Time.deltaTime * scale);
        }

        private void LateUpdate()
        {
            if (!Active || _lanes.Count == 0) return;
            RenderLanes();
            UpdateCameras();
        }

        private void StepLanes(double wallDt)
        {
            double dt = _lanes[0].Loop.Config.PhysicsStepSeconds;
            _accumulator += wallDt;

            int steps = 0;
            while (_accumulator >= dt && steps < 60)
            {
                bool anyAlive = false;
                foreach (var l in _lanes)
                {
                    if (l.Loop.StepPhysics()) anyAlive = true;
                    if (l.Loop.CurrentMinFreeboard < NearRimMargin) l.NearRimSeconds += dt;
                    var gt = l.Loop.LatestGroundTruth;
                    if (gt.SloshKineticEnergy > l.PeakSloshKE) l.PeakSloshKE = gt.SloshKineticEnergy;
                    if (gt.AnyTorqueSaturated) l.TorqueSatSeconds += dt;
                }
                _accumulator -= dt;
                steps++;
                if (!anyAlive) { _finished = true; _accumulator = 0; LogEpisodes(); break; }
            }
        }

        // One row per lane, appended to Logs/episodes.csv — the same file the
        // headless evaluators write, so an in-app comparison run shows up in the
        // spreadsheet next to the batch runs.
        private void LogEpisodes()
        {
            if (_lanes.Count == 0) return;
            try
            {
                using (var log = new FluidStabilizer.Core.Sim.Eval.EpisodeLog(
                    FluidStabilizer.Core.Sim.Eval.EpisodeLog.DefaultPath,
                    $"comparison '{_trace?.Label}' {DateTime.Now:yyyy-MM-dd HH:mm}"))
                {
                    foreach (var l in _lanes)
                        log.Add("comparison", l.Loop.Config, l.Loop.Result(), _trace, l.Controller);
                }
            }
            catch (Exception e) { Debug.LogWarning("[compare] episode log failed: " + e.Message); }
        }

        private void RenderLanes()
        {
            foreach (var l in _lanes)
            {
                if (l.View != null) l.View.RenderTick();
                if (l.Ribbon != null) l.Ribbon.RenderTick();
            }
        }

        /// <summary>Editor capture aid: advance and render as if <paramref name="seconds"/> of wall time passed.</summary>
        public void PumpForCapture(double seconds, double frameDt = 1.0 / 60.0)
        {
            if (!Active) return;
            int frames = Mathf.CeilToInt((float)(seconds / frameDt));
            for (int f = 0; f < frames && !_finished; f++)
            {
                StepLanes(frameDt);
                RenderLanes();
                UpdateCameras();
            }
        }

        private void UpdateCameras()
        {
            float d = Time.deltaTime > 1e-4f ? Time.deltaTime : 0.05f; // headless capture has dt≈0
            float k = 1f - Mathf.Exp(-5f * d);

            foreach (var l in _lanes)
            {
                if (l.Cam == null || l.Rig == null) continue;
                Vector3 p = l.Rig.transform.position;
                Vector3 fwd = l.Rig.transform.forward;

                // A wide-strip chase cam: pulled back and a touch above, looking
                // slightly down at the tank.
                Vector3 target = p - fwd * 11f + Vector3.up * 4.2f;
                Vector3 look = p + Vector3.up * 1.1f + fwd * 2f;
                Quaternion rot = Quaternion.LookRotation(look - target);

                l.Cam.transform.position = Vector3.Lerp(l.Cam.transform.position, target, k);
                l.Cam.transform.rotation = Quaternion.Slerp(l.Cam.transform.rotation, rot, k);
            }
        }

        // ------------------------------------------------------------------- HUD

        private GUIStyle _title, _metric, _big, _hint;

        private void EnsureStyles()
        {
            if (_title != null) return;
            _title = SimGui.Shadowed(15, Color.white, FontStyle.Bold);
            _metric = SimGui.Shadowed(12, new Color(1f, 1f, 1f, 0.85f));
            _big = SimGui.Shadowed(26, Color.white, FontStyle.Bold);
            _hint = SimGui.Shadowed(12, new Color(1f, 1f, 1f, 0.8f));
            _hint.alignment = TextAnchor.MiddleCenter;
        }

        private void OnGUI()
        {
            if (!Active || _lanes.Count == 0) return;
            EnsureStyles();

            int n = _lanes.Count;
            var bestLanes = BestLanes();
            double elapsed = _lanes[0].Loop.SimTime;
            double total = _trace != null ? _trace.DurationSeconds : 0.0;
            float stripH = Screen.height / (float)n;

            for (int i = 0; i < n; i++)
            {
                Lane l = _lanes[i];
                bool isBest = bestLanes.Contains(l.Choice);
                bool offline = l.Controller is RemotePolicyController rp && rp.ExecutionStatus != "policy-ok";

                // thin divider between strips
                if (i > 0) SimGui.Box(new Rect(0, i * stripH - 1, Screen.width, 2), new Color(0, 0, 0, 0.6f));

                var card = new Rect(12, i * stripH + 10, 300, 128);
                SimGui.Panel(card);
                if (isBest) SimGui.Border(card, new Color(0.35f, 0.95f, 0.45f, 0.95f), 2f);

                string title = _laneLabels != null && i < _laneLabels.Length
                    ? _laneLabels[i]
                    : (i < 3 ? "①②③"[i].ToString() + " " : "") + SimulationRunner.ShortName(l.Choice);

                GUILayout.BeginArea(new Rect(card.x + 12, card.y + 8, card.width - 24, card.height - 16));
                GUILayout.Label(title + (offline ? "  [" + ((RemotePolicyController)l.Controller).ExecutionStatus + "]" : ""), _title);
                GUILayout.Space(2);
                var prevC = _big.normal.textColor;
                _big.normal.textColor = isBest ? new Color(0.5f, 1f, 0.55f)
                    : (l.SpillLitres > 0.01 ? new Color(1f, 0.7f, 0.5f) : Color.white);
                GUILayout.Label($"{l.SpillLitres:0.00} L 넘침", _big);
                _big.normal.textColor = prevC;
                GUILayout.Label($"rim 근접 {l.NearRimSeconds:0.0}s   슬로시피크 {l.PeakSloshKE:0}J", _metric);
                GUILayout.Label($"토크포화 {l.TorqueSatSeconds:0.0}s", _metric);
                GUILayout.EndArea();
            }

            // verdict banner — the honest one-liner once every lane has finished
            if (_finished)
            {
                var vb = new Rect(0, Screen.height - 26 - 34, Screen.width, 32);
                SimGui.Box(vb, new Color(0.05f, 0.06f, 0.08f, 0.9f));
                _hint.alignment = TextAnchor.MiddleCenter;
                SimGui.ShadowLabel(vb, VerdictText(), _hint);
            }

            // bottom bar
            var bar = new Rect(0, Screen.height - 26, Screen.width, 26);
            SimGui.Box(bar, new Color(0.05f, 0.06f, 0.08f, 0.8f));
            _hint.alignment = TextAnchor.MiddleCenter;
            SimGui.ShadowLabel(bar, $"동일 주행 '{_trace?.Label}' 을 3가지 방식으로   ·   {elapsed:0.0} / {total:0.0} s" +
                (_finished ? "  — 완료" : "") + "      C: 나가기   R: 다시   T: 슬로모", _hint);
        }

        // "① 강체가 가장 적게 넘침 (…)". Deliberately plain — this is the takeaway a
        // viewer should get without reading the per-lane cards.
        private static bool IsComparable(Lane l) =>
            !(l.Controller is IPolicyExecutionStatus p) || p.ExecutionStatus == "policy-ok";

        /// <summary>
        /// Lanes eligible for the green "winner" highlight: least spill among the
        /// lanes whose controller actually ran. A lane that fell back to Hold (no
        /// policy server) or ran an unverified policy is excluded — otherwise the
        /// fallback lane wins *as the AI*, the exact confusion <see cref="VerdictText"/>
        /// refuses to make. Empty when no lane is comparable.
        /// </summary>
        public List<StabilizerChoice> BestLanes()
        {
            var comparable = _lanes.Where(IsComparable).ToList();
            if (comparable.Count == 0) return new List<StabilizerChoice>();
            double best = comparable.Min(l => l.SpillLitres);
            return comparable.Where(l => l.SpillLitres <= best + 1e-6).Select(l => l.Choice).ToList();
        }

        public string VerdictText()
        {
            var invalid = _lanes.Where(l => l.Controller is IPolicyExecutionStatus p && p.ExecutionStatus != "policy-ok").ToList();
            if (invalid.Count > 0)
                return "AI 성능 비교 제외: " + string.Join(", ", invalid.Select(l => ((IPolicyExecutionStatus)l.Controller).ExecutionStatus))
                    + " — 표시된 스필에는 Hold fallback 또는 미확인 정책 실행이 포함될 수 있음";

            var ranked = _lanes
                .Select((l, i) => (l, tag: i < 3 ? "①②③"[i].ToString() : "·"))
                .OrderBy(t => t.l.SpillLitres)
                .ToList();
            var win = ranked[0];
            string wname = SimulationRunner.ShortName(win.l.Choice);

            if (ranked.Count > 1)
            {
                double gap = ranked[1].l.SpillLitres - win.l.SpillLitres;
                string cmp = gap < 0.05
                    ? "— 레인 간 차이 거의 없음"
                    : $"— 다음 레인보다 {gap:0.0} L 적게 넘침";
                return $"{win.tag} {wname} 이 가장 적게 넘쳤다 ({win.l.SpillLitres:0.0} L) {cmp}";
            }
            return $"{win.tag} {wname}: {win.l.SpillLitres:0.0} L";
        }

        private static void SetComponentEnabled<T>(GameObject go, bool on) where T : Behaviour
        {
            var c = go.GetComponent<T>();
            if (c != null) c.enabled = on;
        }

        // -------------------------------------------------------------- test aid

        /// <summary>Headless: step every lane to the end of the trace and return the per-lane spill (L).</summary>
        public Dictionary<StabilizerChoice, double> RunToEndHeadless()
        {
            while (!_finished && _lanes.Count > 0)
            {
                bool anyAlive = false;
                foreach (var l in _lanes)
                {
                    double dt = l.Loop.Config.PhysicsStepSeconds;
                    if (l.Loop.StepPhysics()) anyAlive = true;
                    if (l.Loop.CurrentMinFreeboard < NearRimMargin) l.NearRimSeconds += dt;
                    var gt = l.Loop.LatestGroundTruth;
                    if (gt.SloshKineticEnergy > l.PeakSloshKE) l.PeakSloshKE = gt.SloshKineticEnergy;
                }
                if (!anyAlive) _finished = true;
            }
            return _lanes.ToDictionary(l => l.Choice, l => l.SpillLitres);
        }
    }
}
