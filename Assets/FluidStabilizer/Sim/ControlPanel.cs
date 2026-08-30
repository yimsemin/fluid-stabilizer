using System;
using FluidStabilizer.Core.Sim;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// In-scene control panel (backquote <c>`</c> toggles it). Edits the shared
    /// <c>experiment.txt</c> profile live — road presets, fill, road character,
    /// grip, driver, slosh model — and rebuilds the running sim on Apply. Works
    /// in the compiled standalone app, not just the Editor (project-goals.md:
    /// "all configuration lives in a Unity control panel").
    /// </summary>
    public sealed class ControlPanel : MonoBehaviour
    {
        public SimulationRunner Runner;
        public ComparisonRunner Comparison;   // optional — Apply also restarts a running comparison

        public bool OpenOnStart = false;

        private bool _open;
        private ExperimentProfile _edit;   // working copy
        private string _status = "";
        private float _statusFade;

        private GUIStyle _h1, _h2, _lbl, _btn, _btnOn;

        public bool IsOpen => _open;
        public void Toggle() => SetOpen(!_open);

        private void Start()
        {
            _edit = ExperimentProfileStore.Load();
            if (OpenOnStart) SetOpen(true);
        }

        private void SetOpen(bool on)
        {
            if (on) _edit = ExperimentProfileStore.Load(); // re-sync from disk each time it opens
            _open = on;
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.backquoteKey.wasPressedThisFrame) Toggle();
            if (_statusFade > 0f) _statusFade -= Time.unscaledDeltaTime;
        }

        // ------------------------------------------------------------------- GUI

        private void EnsureStyles()
        {
            if (_h1 != null) return;
            _h1 = SimGui.Shadowed(17, Color.white, FontStyle.Bold);
            _h2 = SimGui.Shadowed(13, new Color(0.75f, 0.85f, 1f), FontStyle.Bold);
            _lbl = SimGui.Shadowed(13, new Color(1f, 1f, 1f, 0.92f));
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 12 };
            _btnOn = new GUIStyle(_btn) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.55f, 1f, 0.6f) } };
        }

        private void OnGUI()
        {
            EnsureStyles();

            // A slim always-visible hint so the panel is discoverable.
            if (!_open)
            {
                var hint = new Rect(Screen.width - 232, 10, 222, 26);
                SimGui.Panel(hint);
                SimGui.ShadowLabel(new Rect(hint.x + 10, hint.y + 5, hint.width - 20, 18),
                    "`(백틱): 설정 패널", _lbl);
                return;
            }

            float w = 360f, x = Screen.width - w - 10f, y = 10f, h = Mathf.Min(Screen.height - 20f, 560f);
            var panel = new Rect(x, y, w, h);
            SimGui.Panel(panel, 0.92f);

            GUILayout.BeginArea(new Rect(panel.x + 14, panel.y + 12, panel.width - 28, panel.height - 24));

            GUILayout.Label("설정 패널", _h1);
            GUILayout.Label("도로 프리셋", _h2);

            // preset grid
            int i = 0;
            foreach (var (name, _) in ExperimentProfile.Presets)
            {
                if (i % 2 == 0) GUILayout.BeginHorizontal();
                bool cur = _edit.Preset == name;
                if (GUILayout.Button(name, cur ? _btnOn : _btn, GUILayout.Height(24)))
                {
                    _edit.ApplyPreset(name);
                }
                if (i % 2 == 1) GUILayout.EndHorizontal();
                i++;
            }
            if (i % 2 == 1) GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("탱크", _h2);
            _edit.FillRatio = Slider("채움 (fill)", _edit.FillRatio, 0.35, 0.85, "");
            _edit.TankHeightZ = Slider("탱크 높이", _edit.TankHeightZ, 0.4, 0.8, " m");

            GUILayout.Space(6);
            GUILayout.Label("도로", _h2);
            _edit.RoadEnabled = GUILayout.Toggle(_edit.RoadEnabled, "  도로 켜기");
            using (new Disabled(!_edit.RoadEnabled))
            {
                _edit.RoadSeverity = Slider("울퉁불퉁함", _edit.RoadSeverity, 0.0, 2.5, "");
                _edit.RoadCurve = Slider("굽은 정도", _edit.RoadCurve, 0.0, 3.0, "×");
                _edit.RoadHills = Slider("오르내림", _edit.RoadHills, 0.0, 3.0, "×");
                _edit.Grip = Slider("접지력 (↓미끄러움)", _edit.Grip, 0.25, 1.2, "×");
            }

            GUILayout.Space(6);
            GUILayout.Label("운전자 (합성 실행용 — 직접 운전엔 영향 없음)", _h2);
            _edit.Driver = Choice("종류", _edit.Driver, "highway", "aggressive", "scripted");
            _edit.DriverIntensity = Slider("강도", _edit.DriverIntensity, 0.0, 1.0, "");

            GUILayout.Space(6);
            GUILayout.Label("슬로시 모델", _h2);
            _edit.SloshModel = Choice("", _edit.SloshModel, "pendulum", "shallow");

            GUILayout.Space(12);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("적용 (씬 재시작)", _btn, GUILayout.Height(30))) Apply();
            if (GUILayout.Button("되돌리기", _btn, GUILayout.Height(30), GUILayout.Width(90)))
            {
                _edit = ExperimentProfileStore.Load();
                Flash("디스크 값으로 되돌림");
            }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("기본값으로", _btn)) { _edit = ExperimentProfile.Defaults(); Flash("기본값 로드 (아직 미적용)"); }

            if (_statusFade > 0f)
            {
                var c = _lbl.normal.textColor; _lbl.normal.textColor = new Color(0.6f, 1f, 0.65f);
                GUILayout.Label(_status, _lbl);
                _lbl.normal.textColor = c;
            }
            GUILayout.Label("파일: experiment.txt", SimGui.Shadowed(11, new Color(1, 1, 1, 0.5f)));

            GUILayout.EndArea();
        }

        private void Apply()
        {
            try
            {
                _edit.SaveToFile(ExperimentProfileStore.Path);
            }
            catch (Exception e)
            {
                Flash("저장 실패: " + e.Message);
                return;
            }

            if (Comparison != null && Comparison.Active)
            {
                Comparison.Stop();
            }
            if (Runner != null)
            {
                Runner.enabled = true;
                Runner.BuildLoop();
            }
            Flash($"적용됨 — {_edit.Preset}");
        }

        private void Flash(string s) { _status = s; _statusFade = 3.5f; }

        // -------------------------------------------------------------- widgets

        private double Slider(string label, double v, double lo, double hi, string suffix)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _lbl, GUILayout.Width(150));
            float nv = GUILayout.HorizontalSlider((float)v, (float)lo, (float)hi, GUILayout.ExpandWidth(true));
            string txt = suffix == "×" ? "×" + nv.ToString("0.0") : nv.ToString("0.00") + suffix;
            GUILayout.Label(txt, _lbl, GUILayout.Width(60));
            GUILayout.EndHorizontal();
            return nv;
        }

        private string Choice(string label, string cur, params string[] opts)
        {
            GUILayout.BeginHorizontal();
            if (!string.IsNullOrEmpty(label)) GUILayout.Label(label, _lbl, GUILayout.Width(60));
            string result = cur;
            foreach (var o in opts)
                if (GUILayout.Button(o, o == cur ? _btnOn : _btn, GUILayout.Height(22))) result = o;
            GUILayout.EndHorizontal();
            return result;
        }

        private readonly struct Disabled : IDisposable
        {
            private readonly bool _was;
            public Disabled(bool disable) { _was = GUI.enabled; GUI.enabled = _was && !disable; }
            public void Dispose() { GUI.enabled = _was; }
        }
    }
}
