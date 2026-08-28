using System.IO;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Sim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// Headless screenshots of the play scene, for visual review without opening
    /// the editor. Not part of the build.
    ///
    ///   Unity -batchmode -quit -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.SceneCapture.Grab
    ///
    /// (no -nographics — the camera has to render). Writes Logs/shots/*.png.
    /// </summary>
    public static class SceneCapture
    {
        public static void Grab()
        {
            EditorSceneManager.OpenScene("Assets/FluidStabilizer/Scenes/PlayScene.unity");
            var runner = Object.FindObjectOfType<SimulationRunner>();
            var view = Object.FindObjectOfType<SimView>();
            var cam = Object.FindObjectOfType<Camera>();
            Directory.CreateDirectory("Logs/shots");

            Shoot(runner, view, cam, SloshModelChoice.ShallowWater, "shallow", 0f, false);
            Shoot(runner, view, cam, SloshModelChoice.PendulumRom, "pendulum", 0f, false);
            Shoot(runner, view, cam, SloshModelChoice.ShallowWater, "shallow-front", 55f, false);
            Shoot(runner, view, cam, SloshModelChoice.ShallowWater, "shallow-calm", 0f, true);
        }

        private static void Shoot(SimulationRunner runner, SimView view, Camera cam,
            SloshModelChoice model, string tag, float yaw, bool calm)
        {
            runner.SloshModel = model;
            runner.DriverOverride = calm
                ? (IDriverInput)new HighwayDriver(1, briskness: 0.5)
                : new ScriptedManeuverDriver();
            runner.BuildLoop();

            double maxPitch = 0, maxRoll = 0;
            for (int i = 0; i < 420; i++) // ~7 s sim time
            {
                runner.Advance(1.0 / 60.0);
                view.RenderTick();
                var g = runner.Loop.GimbalState;
                maxPitch = System.Math.Max(maxPitch, System.Math.Abs(g.Pitch.Angle));
                maxRoll = System.Math.Max(maxRoll, System.Math.Abs(g.Roll.Angle));
            }
            var gt = runner.Loop.LatestGroundTruth;
            Debug.Log($"[SceneCapture] {tag}: maxPitch={maxPitch * 57.3:F1}deg maxRoll={maxRoll * 57.3:F1}deg " +
                      $"spill={gt.CumulativeOverflowVolume * 1000:F1}L worstFB={gt.MinFreeboard:F3} " +
                      $"angleLimited={gt.AnyAngleLimited}");

            Transform t = view.TruckBody;
            Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * (-t.right);
            cam.transform.position = t.position + dir * 5.5f + Vector3.up * 3.2f;
            cam.transform.rotation = Quaternion.LookRotation(
                t.position + Vector3.up * 2.3f - cam.transform.position);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.62f, 0.72f);

            var rt = new RenderTexture(1280, 800, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 800, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 800), 0, 0);
            tex.Apply();
            File.WriteAllBytes($"Logs/shots/{tag}.png", tex.EncodeToPNG());
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Debug.Log($"[SceneCapture] wrote Logs/shots/{tag}.png");
        }
    }
}
