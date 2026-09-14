using System.Collections.Generic;
using FluidStabilizer.Core.Sim.Drivers;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// One guided, ordered screen for the "청중이 운전 → 그 데이터로 재학습" demo
    /// beat — a single button instead of hopping between several menu items, with
    /// a real progress bar so a live audience sees the wait is going somewhere
    /// (<see cref="LiveTrainingPipeline"/> does the actual work on a background
    /// thread). Toggle with <c>K</c>. Editor-only, like the policy server buttons
    /// in <see cref="ControlPanel"/> — it launches the same <c>python</c> process.
    /// </summary>
    public sealed class LiveTrainingPanel : MonoBehaviour
    {
        public ControlPanel ControlPanel;

        private bool _open;
        private readonly LiveTrainingPipeline _pipeline = new LiveTrainingPipeline();
        private int _usableCount = -1;
        private float _recheckIn;

        private GUIStyle _h1, _h2, _lbl, _dim, _btn, _bar, _barFillOk, _barFillErr, _warn, _hint;

        public bool IsOpen => _open;
        public void Toggle() => _open = !_open;

        private void EnsureStyles()
        {
            if (_h1 != null) return;
            _h1 = SimGui.Shadowed(17, Color.white, FontStyle.Bold);
            _h2 = SimGui.Shadowed(13, new Color(0.75f, 0.85f, 1f), FontStyle.Bold);
            _lbl = SimGui.Shadowed(13, new Color(1f, 1f, 1f, 0.92f));
            _dim = SimGui.Shadowed(12, new Color(1f, 1f, 1f, 0.65f));
            _warn = SimGui.Shadowed(13, new Color(1f, 0.55f, 0.4f));
            _hint = SimGui.Shadowed(12, new Color(1f, 1f, 1f, 0.55f));
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.kKey.wasPressedThisFrame) Toggle();

            if (!_pipeline.Running)
            {
                _recheckIn -= Time.unscaledDeltaTime;
                if (_recheckIn <= 0f)
                {
                    _recheckIn = 1.5f;
                    _usableCount = LiveTrainingPipeline.UsableTraces().Count;
                }
            }
        }

        private void OnGUI()
        {
            if (!_open) return;
            EnsureStyles();
            if (!Application.isEditor)
            {
                var badge = new Rect(Screen.width * 0.5f - 190, 46, 380, 26);
                SimGui.Panel(badge);
                SimGui.ShadowLabel(new Rect(badge.x + 10, badge.y + 5, badge.width - 20, 18),
                    "라이브 학습은 에디터 전용 — 빌드에선 사용 불가", _warn);
                return;
            }

            float w = 380f, x = 10f, y = 10f, h = Screen.height - 20f;
            var panel = new Rect(x, y, w, h);
            SimGui.Panel(panel, 0.92f);

            GUILayout.BeginArea(new Rect(panel.x + 14, panel.y + 12, panel.width - 28, panel.height - 24));
            GUILayout.Label("라이브 학습   (K 로 닫기)", _h1);
            GUILayout.Label("청중이 운전한 주행을 포함해, 지금까지 녹화된 모든 주행으로 정책을 새로 학습합니다.", _dim);
            GUILayout.Space(10);

            LiveTrainingPipeline.Snapshot snap = _pipeline.GetSnapshot();

            DrawSteps(snap);
            GUILayout.Space(10);

            DrawProgressBar(snap);
            GUILayout.Space(8);

            string statusText = snap.Stage == LiveTrainingPipeline.Stage.Failed
                ? "실패: " + snap.Error
                : (string.IsNullOrEmpty(snap.Status) ? "대기 중" : snap.Status);
            GUILayout.Label(statusText, snap.Stage == LiveTrainingPipeline.Stage.Failed ? _warn : _lbl);

            GUILayout.Space(12);

            if (_usableCount < 0) _usableCount = LiveTrainingPipeline.UsableTraces().Count;
            GUILayout.Label($"사용 가능한 주행: {_usableCount}건 (P 로 녹화 · 도로 켜짐 + fill≥0.60 필요)", _dim);

            GUILayout.Space(6);
            using (new Disabled(_pipeline.Running || _usableCount == 0))
            {
                if (GUILayout.Button(_pipeline.Running ? "학습 중…" : "학습 시작", _btn, GUILayout.Height(34)))
                    _pipeline.Start();
            }

            if (snap.Stage == LiveTrainingPipeline.Stage.Done)
            {
                GUILayout.Space(10);
                GUILayout.Label("새 체크포인트: " + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(snap.CheckpointPath)) + "/actor.pt", _dim);
                if (ControlPanel != null && GUILayout.Button("정책 서버 (재)시작 → ③ 레인에 반영", _btn, GUILayout.Height(28)))
                    ControlPanel.RestartServer();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("순서: P 로 청중 운전 녹화(여러 번 가능) → 학습 시작 → 완료되면 서버 재시작 → 4 또는 C 로 확인", _hint);
            GUILayout.EndArea();
        }

        private void DrawSteps(LiveTrainingPipeline.Snapshot snap)
        {
            var stage = snap.Stage;
            bool failed = stage == LiveTrainingPipeline.Stage.Failed;
            bool Beyond(LiveTrainingPipeline.Stage s) => !failed && stage > s;

            DrawStep("①", "청중 운전 녹화 (P)", true, false); // always "ready" — happens outside the pipeline
            DrawStep("②", "천장 탐색 (hindsight)", Beyond(LiveTrainingPipeline.Stage.Hindsight), stage == LiveTrainingPipeline.Stage.Hindsight);
            DrawStep("③", "학습 데이터셋 만들기", Beyond(LiveTrainingPipeline.Stage.BcExport), stage == LiveTrainingPipeline.Stage.BcExport);
            DrawStep("④", "정책 학습 (BC)", Beyond(LiveTrainingPipeline.Stage.BcTrain), stage == LiveTrainingPipeline.Stage.BcTrain);
            DrawStep("⑤", "완료 — 서버에 반영", stage == LiveTrainingPipeline.Stage.Done, false);
        }

        private void DrawStep(string tag, string label, bool done, bool active)
        {
            GUILayout.BeginHorizontal();
            string icon = done ? "✓" : (active ? "●" : "○");
            var style = done ? new GUIStyle(_lbl) { normal = { textColor = new Color(0.55f, 1f, 0.6f) } }
                       : active ? new GUIStyle(_lbl) { normal = { textColor = new Color(1f, 0.85f, 0.4f) } }
                       : _dim;
            GUILayout.Label($"{icon} {tag} {label}", style);
            GUILayout.EndHorizontal();
        }

        private void DrawProgressBar(LiveTrainingPipeline.Snapshot snap)
        {
            var rect = GUILayoutUtility.GetRect(10, 22, GUILayout.ExpandWidth(true));
            bool failed = snap.Stage == LiveTrainingPipeline.Stage.Failed;
            SimGui.Box(rect, new Color(1f, 1f, 1f, 0.12f));
            var fill = new Rect(rect.x, rect.y, rect.width * snap.Progress, rect.height);
            SimGui.Box(fill, failed ? new Color(0.85f, 0.4f, 0.3f, 0.9f)
                : snap.Stage == LiveTrainingPipeline.Stage.Done ? new Color(0.4f, 0.85f, 0.5f, 0.9f)
                : new Color(0.4f, 0.65f, 0.95f, 0.9f));
            var pctStyle = new GUIStyle(_lbl) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            GUI.Label(rect, $"{snap.Progress * 100f:0}%", pctStyle);
        }

        private readonly struct Disabled : System.IDisposable
        {
            private readonly bool _was;
            public Disabled(bool disable) { _was = GUI.enabled; GUI.enabled = _was && !disable; }
            public void Dispose() { GUI.enabled = _was; }
        }
    }
}
