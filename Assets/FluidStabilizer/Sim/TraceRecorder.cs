using System;
using System.IO;
using FluidStabilizer.Core.Sim.Drivers;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Records the owner's driving to a <see cref="DriverTrace"/> file and replays
    /// saved traces — phase-1 item 3 (design-review §17, project-goals ③). A
    /// recorded drive is the real disturbance data for training and a held-out
    /// condition for the three-way comparison.
    ///
    /// Keys:  P  start / stop recording (writes a file on stop)
    ///        L  replay the newest saved trace  /  press again to hand back to you
    ///
    /// A status banner across the top of the screen always shows whether it is
    /// idle, recording, or replaying. Files go to <c>&lt;project&gt;/Traces/</c>
    /// in the Editor (<c>&lt;persistentDataPath&gt;/traces/</c> in a build); the
    /// full path is logged to the Console on every save.
    /// </summary>
    public sealed class TraceRecorder : MonoBehaviour
    {
        public SimulationRunner Runner;

        public enum Mode { Live, Recording, Replaying }
        public Mode CurrentMode { get; private set; } = Mode.Live;

        private RecordingDriver _recorder;
        private DriverTrace _replayTrace;
        private string _lastSavedPath;
        private string _statusNote = "";

        private string TraceDir => TraceStore.Dir;

        private ComparisonRunner _comparison;

        private void Awake()
        {
            if (Runner == null) Runner = FindAnyObjectByType<SimulationRunner>();
            _comparison = GetComponent<ComparisonRunner>() ?? FindAnyObjectByType<ComparisonRunner>();
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || Runner == null) return;
            if (_comparison != null && _comparison.Active) return; // comparison mode owns the keys

            if (kb.pKey.wasPressedThisFrame)
            {
                if (CurrentMode == Mode.Recording) StopRecording();
                else if (CurrentMode == Mode.Replaying) _statusNote = "press L to leave replay before recording";
                else StartRecording();
            }

            if (kb.lKey.wasPressedThisFrame)
            {
                if (CurrentMode == Mode.Replaying) BackToLive();
                else if (CurrentMode == Mode.Recording) _statusNote = "press P to stop recording first";
                else ReplayNewest();
            }
        }

        // ---------------------------------------------------------------- record

        public void StartRecording()
        {
            var trace = new DriverTrace { Label = "drive-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) };
            _recorder = new RecordingDriver(new HumanDriverInput(), trace);
            Runner.ReplayTrace = null;
            Runner.DriverOverride = _recorder;
            Runner.BuildLoop();
            // Capture the built loop, after profile application, before its first physics step.
            trace.CaptureConfig(Runner.Loop.Config, Runner.ActiveSeed,
                Runner.ActiveSloshModel == SloshModelChoice.ShallowWater ? "shallow" : "pendulum",
                Runner.ActiveStabilizer.ToString());
            CurrentMode = Mode.Recording;
            _statusNote = "";

            TraceStore.EnsureDir();
            Debug.Log($"[trace] ● RECORDING '{trace.Label}' — drive now, press P again to stop. → {TraceDir}");
        }

        public void StopRecording()
        {
            if (_recorder == null) return;

            DriverTrace trace = _recorder.Trace;
            TraceStore.EnsureDir();
            _lastSavedPath = TraceStore.PathFor(trace.Label);
            trace.SaveToFile(_lastSavedPath);
            Debug.Log($"[trace] ■ SAVED {trace.Count} steps ({trace.DurationSeconds:F1} s) → {_lastSavedPath}");

            _recorder = null;
            Runner.DriverOverride = null;
            Runner.BuildLoop();
            CurrentMode = Mode.Live;
            _statusNote = "saved " + Path.GetFileName(_lastSavedPath);
        }

        // ---------------------------------------------------------------- replay

        public void ReplayNewest()
        {
            string path = NewestTraceFile();
            if (path == null)
            {
                _statusNote = "no saved traces in " + TraceDir;
                Debug.LogWarning("[trace] " + _statusNote);
                return;
            }
            ReplayFile(path);
        }

        public void ReplayFile(string path)
        {
            DriverTrace trace = DriverTrace.LoadFromFile(path);

            _replayTrace = trace;
            Runner.ReplayTrace = trace;
            Runner.DriverOverride = null;
            Runner.BuildLoop();
            CurrentMode = Mode.Replaying;
            _statusNote = string.IsNullOrEmpty(trace.Assumptions) ? "" : "Legacy replay: missing settings assumed; see Console";
            if (_statusNote.Length > 0) Debug.LogWarning("[trace] " + trace.Assumptions);
            Debug.Log($"[trace] ▶ REPLAY '{trace.Label}' ({trace.Count} steps, {trace.DurationSeconds:F1} s) — try 0-3 to switch stabilizer; L to drive again.");
        }

        public void BackToLive()
        {
            Runner.DriverOverride = null;
            Runner.ReplayTrace = null;
            _replayTrace = null;
            Runner.BuildLoop();
            CurrentMode = Mode.Live;
            _statusNote = "";
            Debug.Log("[trace] back to live driving.");
        }

        private string NewestTraceFile() => TraceStore.NewestFile();

        // ------------------------------------------------------------------- HUD

        private GUIStyle _pill, _sub;

        private void EnsureStyles()
        {
            if (_pill != null) return;
            _pill = new GUIStyle(GUI.skin.label)
            {
                fontSize = 19, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            _sub = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.75f) }
            };
        }

        private void OnGUI()
        {
            if (_comparison != null && _comparison.Active) return; // ComparisonRunner draws its own HUD
            EnsureStyles();

            double elapsed = Runner?.Loop?.SimTime ?? 0.0;
            string main;
            Color bg;
            switch (CurrentMode)
            {
                case Mode.Recording:
                    bool blink = ((int)(Time.realtimeSinceStartup * 2f) & 1) == 0;
                    int steps = _recorder?.Trace.Count ?? 0;
                    main = $"{(blink ? "●" : "  ")}  REC   {Fmt(elapsed)}   {steps} steps      [P] stop & save";
                    bg = new Color(0.75f, 0.12f, 0.12f, 0.92f);
                    break;
                case Mode.Replaying:
                    double dur = _replayTrace?.DurationSeconds ?? 0.0;
                    bool done = Runner?.Loop?.EpisodeComplete ?? false;
                    main = done
                        ? $"▶ REPLAY '{_replayTrace?.Label}'  —  complete ({Fmt(dur)})      [R] again   [L] drive"
                        : $"▶ REPLAY '{_replayTrace?.Label}'   {Fmt(elapsed)} / {Fmt(dur)}      [L] drive";
                    bg = new Color(0.12f, 0.4f, 0.7f, 0.92f);
                    break;
                default:
                    main = "TRACE  ●  idle       [P] record your drive       [L] replay newest";
                    bg = new Color(0f, 0f, 0f, 0.6f);
                    break;
            }

            const float w = 720f, h = 30f;
            var r = new Rect((Screen.width - w) * 0.5f, 6f, w, h);
            DrawRect(r, bg);
            GUI.Label(r, main, _pill);

            if (CurrentMode == Mode.Replaying && _replayTrace != null && _replayTrace.DurationSeconds > 0)
            {
                float p = Mathf.Clamp01((float)(elapsed / _replayTrace.DurationSeconds));
                DrawRect(new Rect(r.x, r.yMax, w, 3f), new Color(1f, 1f, 1f, 0.25f));
                DrawRect(new Rect(r.x, r.yMax, w * p, 3f), new Color(0.6f, 0.85f, 1f, 0.95f));
            }

            if (!string.IsNullOrEmpty(_statusNote))
                GUI.Label(new Rect(r.x, r.yMax + 4f, w, 16f), _statusNote, _sub);

            if (CurrentMode == Mode.Recording)
            {
                DrawRect(new Rect(0, 0, Screen.width, 3f), new Color(0.9f, 0.15f, 0.15f, 0.9f));
                DrawRect(new Rect(0, Screen.height - 3f, Screen.width, 3f), new Color(0.9f, 0.15f, 0.15f, 0.9f));
            }
        }

        private static string Fmt(double s)
        {
            if (s < 0) s = 0;
            return $"{(int)(s / 60):00}:{s % 60:00.0}";
        }

        private static Texture2D _px;

        private static void DrawRect(Rect r, Color c)
        {
            if (_px == null) { _px = new Texture2D(1, 1); _px.SetPixel(0, 0, Color.white); _px.Apply(); }
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _px);
            GUI.color = prev;
        }
    }
}
