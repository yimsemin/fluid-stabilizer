using System;
using FluidStabilizer.Core.Sim;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>Immediate-mode HUD: speed, g, freeboard, spill, active stabilizer, controls.</summary>
    public sealed class Hud : MonoBehaviour
    {
        public SimulationRunner Runner;

        private GUIStyle _label, _big, _warn;
        private float _spillFlash;
        private double _lastSpill;

        private void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 15, normal = { textColor = Color.white } };
            _big = new GUIStyle(_label) { fontSize = 26, fontStyle = FontStyle.Bold };
            _warn = new GUIStyle(_big) { normal = { textColor = new Color(1f, 0.4f, 0.3f) } };
        }

        private void OnGUI()
        {
            if (Runner?.Loop == null || !Runner.enabled) return; // hidden during a side-by-side comparison
            EnsureStyles();

            var loop = Runner.Loop;
            var gt = loop.LatestGroundTruth;
            var cfg = loop.Config;

            double spill = gt.CumulativeOverflowVolume * 1000.0;
            if (spill > _lastSpill + 1e-6) _spillFlash = 1f;
            _lastSpill = spill;
            _spillFlash = Mathf.Max(0f, _spillFlash - Time.deltaTime * 1.5f);

            double freeboard = gt.MinFreeboard;
            double aLong = gt.VehicleLongAccel / 9.80665;
            double aLat = gt.VehicleLatAccel / 9.80665;

            var topPanel = new Rect(12, 10, 388, 340);
            SimGui.Panel(topPanel);
            GUILayout.BeginArea(new Rect(topPanel.x + 12, topPanel.y + 10, topPanel.width - 24, topPanel.height - 20));
            GUILayout.Label($"{loop.VehicleState.Speed * 3.6:F0} km/h", _big);
            GUILayout.Label($"accel  {aLong:+0.00;-0.00} g   lateral {aLat:+0.00;-0.00} g", _label);
            if (cfg.Road.Enabled)
                GUILayout.Label($"road  grade {Math.Tan(gt.RoadGrade) * 100:+0.0;-0.0}%   " +
                                $"bank {gt.RoadBank * 57.29578:+0.0;-0.0}°   " +
                                $"surf {gt.RoadSurfaceHeight * 100:+0.0;-0.0} cm", _label);
            GUILayout.Space(6);

            var fbStyle = freeboard < 0.05 ? _warn : _big;
            GUILayout.Label($"freeboard  {freeboard * 100:F1} cm", fbStyle);
            GUILayout.Label(Bar(freeboard, 0.0, cfg.Slosh.TankHeightZ * (1 - cfg.Slosh.FillRatio)), _label);
            GUILayout.Space(6);

            var spillStyle = _spillFlash > 0f ? _warn : _label;
            GUILayout.Label($"spilled  {spill:F2} L" + (_spillFlash > 0f ? "   ▲ SPILL" : ""), spillStyle);
            GUILayout.Label($"slosh energy  {gt.SloshKineticEnergy:F1} J", _label);
            GUILayout.Space(8);

            string stab = Runner.ActiveStabilizer == StabilizerChoice.LearnedPolicy
                ? loop.Controller.Name                       // shows "@host:port" or "offline → Hold"
                : Runner.ActiveStabilizer.ToString();
            GUILayout.Label($"stabilizer: {stab}" + (Runner.TimeScale < 1f ? "   [slow-mo]" : ""), _label);
            GUILayout.Label($"슬로시 모델: {loop.SloshModel.Name}", _label);

            var prof = Runner.ActiveProfile;
            if (prof != null)
            {
                GUILayout.Space(2);
                GUILayout.Label($"프리셋: {prof.Preset}", _label);
                GUILayout.Label(
                    $"fill {prof.FillRatio:0.00}   " +
                    (prof.RoadEnabled
                        ? $"도로 거칠 {prof.RoadSeverity:0.0} · 굽음 ×{prof.RoadCurve:0.0} · 경사 ×{prof.RoadHills:0.0} · 접지 ×{prof.Grip:0.00}"
                        : "도로 없음"),
                    _label);
                GUILayout.Label("`(백틱): 설정 변경", _label);
            }
            else
            {
                GUILayout.Label($"profile: fill {cfg.Slosh.FillRatio:0.00}   " +
                                $"road {(cfg.Road.Enabled ? $"on ×{cfg.Road.Severity:0.0}" : "off")}", _label);
            }
            GUILayout.EndArea();

            var botPanel = new Rect(12, Screen.height - 96, Mathf.Min(940, Screen.width - 24), 86);
            SimGui.Panel(botPanel);
            GUILayout.BeginArea(new Rect(botPanel.x + 12, botPanel.y + 8, botPanel.width - 24, botPanel.height - 16));
            GUILayout.Label("W/S 또는 트리거: 가감속   A/D 또는 스틱: 조향   Space: 스태빌라이저 on/off", _label);
            GUILayout.Label("0-4: 스태빌라이저 선택 (4 = 학습, serve_policy.py 필요)   M: 슬로시 모델   G: 도로   R: 리셋   T: 슬로모   Tab: 카메라", _label);
            GUILayout.Label("P: 주행 녹화   L: 최신 재생   C: ①②③ 비교   `(백틱): 컨트롤 패널", _label);
            GUILayout.EndArea();
        }

        private static string Bar(double v, double lo, double hi)
        {
            int n = 24;
            double f = hi > lo ? Math.Max(0, Math.Min(1, (v - lo) / (hi - lo))) : 0;
            int k = (int)Math.Round(f * n);
            return "[" + new string('|', k) + new string('.', n - k) + "]";
        }
    }
}
