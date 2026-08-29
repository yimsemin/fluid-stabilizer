using System.Collections.Generic;
using System.Linq;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// The headline comparison (experiment-plan.md step 2): replays one recorded
    /// drive through several stabilizers <em>at once</em>, side by side, over the
    /// same road — so "which tank keeps the water in?" is a direct visual answer.
    /// Every lane is its own deterministic <see cref="SimulationLoop"/> fed the
    /// same <see cref="DriverTrace"/>; a per-lane panel shows litres spilled,
    /// time spent near the rim, and peak slosh energy, with the best lane
    /// highlighted.
    ///
    /// Toggle with <c>C</c> in the play scene (uses the newest recorded trace).
    /// While active the normal single-sim runner is paused.
    /// </summary>
    public sealed class ComparisonRunner : MonoBehaviour
    {
        public SimulationRunner Runner;      // paused while a comparison runs
        public Transform RigTemplate;        // the SceneBuilder TruckRig — cloned per lane
        public Camera Camera;

        [Tooltip("One lane per entry. RL joins once an ONNX inference path exists (phase 1.5).")]
        public StabilizerChoice[] Lanes =
        {
            StabilizerChoice.Off,                       // ① rigid tank
            StabilizerChoice.SpecificForceAlignmentPlusI, // ② rule-based (PID)
            StabilizerChoice.PrivilegedFeedforward,      // privileged reference
        };

        public float LaneSpacing = 4.2f;

        /// <summary>Freeboard (m) below which a lane is "near the rim".</summary>
        public double NearRimMargin = 0.05;

        public bool Active { get; private set; }

        private sealed class Lane
        {
            public StabilizerChoice Choice;
            public SimulationLoop Loop;
            public GameObject Rig;
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

        public void StartFromNewestTrace()
        {
            string path = TraceStore.NewestFile();
            if (path == null)
            {
                Debug.LogWarning("[compare] no recorded traces in " + TraceStore.Dir + " — press P to record one first.");
                return;
            }
            StartComparison(DriverTrace.LoadFromFile(path));
        }

        public void StartComparison(DriverTrace trace) => StartComparison(trace, withRigs: true);

        /// <summary><paramref name="withRigs"/> = false skips all Unity scene objects — for headless tests.</summary>
        public void StartComparison(DriverTrace trace, bool withRigs)
        {
            Stop();
            _trace = trace;
            _finished = false;
            _accumulator = 0;

            if (withRigs)
            {
                if (Runner != null)
                {
                    Runner.enabled = false;
                    SetComponentEnabled<SimView>(Runner.gameObject, false);
                    SetComponentEnabled<RoadRibbon>(Runner.gameObject, false);
                }
                if (RigTemplate != null) RigTemplate.gameObject.SetActive(false);
            }

            float x0 = -(Lanes.Length - 1) * 0.5f * LaneSpacing;
            for (int i = 0; i < Lanes.Length; i++)
            {
                var loop = new SimulationLoop(
                    trace.ToConfig(),
                    SimulationRunner.MakeController(Lanes[i]),
                    new RecordedTraceDriver(trace),
                    trace.MakeSloshModel());
                loop.BeginEpisode(trace.Seed);

                var lane = new Lane { Choice = Lanes[i], Loop = loop };

                if (withRigs && RigTemplate != null)
                {
                    var rig = Instantiate(RigTemplate.gameObject);
                    rig.name = "CompareLane_" + Lanes[i];
                    rig.SetActive(true);

                    var view = rig.AddComponent<SimView>();
                    view.TruckBody = rig.transform;
                    view.TankPivot = rig.transform.Find("TankPivot");
                    view.TankBody = view.TankPivot.Find("TankBody");
                    view.LiquidMesh = view.TankBody.Find("LiquidMesh").GetComponent<MeshFilter>();
                    view.LoopOverride = loop;
                    view.WorldOffset = new Vector3(x0 + i * LaneSpacing, 0f, 0f);

                    var ribbon = rig.AddComponent<RoadRibbon>();
                    ribbon.LoopOverride = loop;
                    ribbon.WorldOffset = view.WorldOffset;

                    lane.Rig = rig;
                }

                _lanes.Add(lane);
            }

            Active = true;
            Debug.Log($"[compare] '{trace.Label}' — {_lanes.Count} lanes: {string.Join(", ", Lanes.Select(SimulationRunner.ShortName))}");
        }

        public void Stop()
        {
            foreach (var l in _lanes)
                if (l.Rig != null) Destroy(l.Rig);
            _lanes.Clear();

            if (!Active) return;
            Active = false;

            if (RigTemplate != null) RigTemplate.gameObject.SetActive(true);
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

            double dt = _lanes[0].Loop.Config.PhysicsStepSeconds;
            float scale = Runner != null ? Mathf.Max(0.05f, Runner.TimeScale) : 1f;
            _accumulator += Time.deltaTime * scale;

            int steps = 0;
            while (_accumulator >= dt && steps < 40)
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
                if (!anyAlive) { _finished = true; _accumulator = 0; break; }
            }

            UpdateCamera();
        }

        private void UpdateCamera()
        {
            if (Camera == null || _lanes.Count == 0 || _lanes[0].Rig == null) return;
            Transform lead = _lanes[0].Rig.transform;
            float span = (Lanes.Length - 1) * LaneSpacing;

            Vector3 focus = new Vector3(0f, 1.6f, lead.position.z + 1f);
            Vector3 target = focus + new Vector3(0f, 4.5f + span * 0.22f, -9f - span * 0.5f);
            Quaternion rot = Quaternion.LookRotation(focus - target);

            float k = 1f - Mathf.Exp(-6f * Time.deltaTime);
            Camera.transform.position = Vector3.Lerp(Camera.transform.position, target, k);
            Camera.transform.rotation = Quaternion.Slerp(Camera.transform.rotation, rot, k);
        }

        // ------------------------------------------------------------------- HUD

        private GUIStyle _title, _metric, _big, _hint;

        private void EnsureStyles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _metric = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = new Color(1f, 1f, 1f, 0.85f) } };
            _big = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _hint = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 1f, 1f, 0.6f) } };
        }

        private void OnGUI()
        {
            if (!Active || _lanes.Count == 0) return;
            EnsureStyles();

            double best = _lanes.Min(l => l.SpillLitres);
            double elapsed = _lanes[0].Loop.SimTime;
            double total = _trace != null ? _trace.DurationSeconds : 0.0;

            float panelW = 236f, panelH = 132f, gap = 10f;
            float totalW = _lanes.Count * panelW + (_lanes.Count - 1) * gap;
            float x = (Screen.width - totalW) * 0.5f;
            float y = 44f;

            GUI.Label(new Rect(0, 20f, Screen.width, 20f),
                $"COMPARE  '{_trace?.Label}'   {elapsed:0.0} / {total:0.0} s" +
                (_finished ? "   — done" : ""), _hint);

            foreach (var l in _lanes)
            {
                var r = new Rect(x, y, panelW, panelH);
                bool isBest = l.SpillLitres <= best + 1e-6;
                DrawRect(r, new Color(0f, 0f, 0f, 0.72f));
                if (isBest) DrawBorder(r, new Color(0.35f, 0.9f, 0.4f, 0.95f), 2f);

                GUILayout.BeginArea(new Rect(r.x + 12, r.y + 8, r.width - 24, r.height - 16));
                GUILayout.Label(SimulationRunner.ShortName(l.Choice), _title);
                GUILayout.Space(2);
                GUILayout.Label($"{l.SpillLitres:0.00} L", isBest ? Green(_big) : _big);
                GUILayout.Label("spilled", _metric);
                GUILayout.Space(4);
                GUILayout.Label($"near rim   {l.NearRimSeconds:0.0} s", _metric);
                GUILayout.Label($"peak slosh {l.PeakSloshKE:0} J", _metric);
                GUILayout.Label($"torque sat {l.TorqueSatSeconds:0.0} s", _metric);
                GUILayout.EndArea();

                x += panelW + gap;
            }

            GUI.Label(new Rect(0, y + panelH + 6f, Screen.width, 18f),
                "C: exit compare    R: restart    T: slow-mo", _hint);
        }

        private GUIStyle Green(GUIStyle s) => new GUIStyle(s) { normal = { textColor = new Color(0.5f, 1f, 0.55f) } };

        private static void SetComponentEnabled<T>(GameObject go, bool on) where T : Behaviour
        {
            var c = go.GetComponent<T>();
            if (c != null) c.enabled = on;
        }

        // ----------------------------------------------------------- GUI helpers

        private static Texture2D _px;

        private static void DrawRect(Rect r, Color c)
        {
            if (_px == null) { _px = new Texture2D(1, 1); _px.SetPixel(0, 0, Color.white); _px.Apply(); }
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _px);
            GUI.color = prev;
        }

        private static void DrawBorder(Rect r, Color c, float t)
        {
            DrawRect(new Rect(r.x, r.y, r.width, t), c);
            DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            DrawRect(new Rect(r.x, r.y, t, r.height), c);
            DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
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
