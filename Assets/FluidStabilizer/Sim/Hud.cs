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
            if (Runner?.Loop == null) return;
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

            GUILayout.BeginArea(new Rect(16, 12, 360, 260));
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

            GUILayout.Label($"stabilizer: {Runner.ActiveStabilizer}" +
                            (Runner.TimeScale < 1f ? "   [slow-mo]" : ""), _label);
            GUILayout.Label($"slosh model: {loop.SloshModel.Name}", _label);
            GUILayout.EndArea();

            GUILayout.BeginArea(new Rect(16, Screen.height - 92, 760, 88));
            GUILayout.Label("W/S or triggers: throttle/brake   A/D or stick: steer", _label);
            GUILayout.Label("Space: stabilizer on/off   0-3: pick stabilizer   M: slosh model   G: road   R: reset   T: slow-mo   Tab: camera", _label);
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
