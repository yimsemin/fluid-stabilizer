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
            var runner = Object.FindAnyObjectByType<SimulationRunner>();
            var view = Object.FindAnyObjectByType<SimView>();
            var cam = Object.FindAnyObjectByType<Camera>();
            Directory.CreateDirectory("Logs/shots");

            Shoot(runner, view, cam, SloshModelChoice.ShallowWater, "shallow", 0f, false);
            Shoot(runner, view, cam, SloshModelChoice.PendulumRom, "pendulum", 0f, false);
            Shoot(runner, view, cam, SloshModelChoice.ShallowWater, "shallow-front", 55f, false);
            Shoot(runner, view, cam, SloshModelChoice.ShallowWater, "shallow-calm", 0f, true);
        }

        /// <summary>Road + side-by-side comparison shots (experiment-plan step 2). Writes Logs/shots/road-*.png.</summary>
        public static void GrabRoad()
        {
            EditorSceneManager.OpenScene("Assets/FluidStabilizer/Scenes/PlayScene.unity");
            var runner = Object.FindAnyObjectByType<SimulationRunner>();
            var view = Object.FindAnyObjectByType<SimView>();
            var ribbon = Object.FindAnyObjectByType<RoadRibbon>();
            var comparison = Object.FindAnyObjectByType<ComparisonRunner>();
            var cam = Object.FindAnyObjectByType<Camera>();
            Directory.CreateDirectory("Logs/shots");

            // --- single-sim, road on ---
            runner.Road = true;
            runner.RoadSeverity = 1.2f;
            runner.DriverOverride = new HighwayDriver(3, briskness: 0.55);
            runner.BuildLoop();
            for (int i = 0; i < 720; i++) { runner.Advance(1.0 / 60.0); view.RenderTick(); ribbon.RenderTick(); }

            Transform t = view.TruckBody;
            ChaseCam(cam, t, 30f);
            Write(cam, "road-single-chase");
            ChaseCam(cam, t, 0f, up: 9f, back: 3f); // near-top, see the curve + markings
            Write(cam, "road-single-top");
            // side-on — checks the truck actually sits on the road surface
            cam.transform.position = t.position + t.right * 14f + t.forward * 2f + Vector3.up * 4f;
            cam.transform.rotation = Quaternion.LookRotation(t.position + Vector3.up * 1.2f - cam.transform.position);
            Write(cam, "road-single-side");

            // --- comparison on a road-on trace ---
            var trace = RecordRoadTrace(seed: 7, seconds: 22.0);
            comparison.StartComparison(trace);
            comparison.PumpForCapture(14.0);
            Write(comparison.FirstStripCamera ?? cam, "road-compare");   // split-screen: capture strip ①
            comparison.PumpForCapture(6.0);
            Write(comparison.FirstStripCamera ?? cam, "road-compare-late");
            comparison.Stop();
        }

        /// <summary>
        /// Frames for the README GIF: ① 강체 vs ② 규칙기반 on one recorded drive, one PNG per lane per step
        /// plus a spill CSV. Args: -capTrace &lt;trace path&gt; [-capSeconds 40] [-capStep 0.5]. Writes
        /// Logs/gif/. Needs the editor graphics (no -nographics); Python assembles the GIF.
        /// </summary>
        public static void GrabCompareFrames()
        {
            string tracePath = Arg("-capTrace", null);
            double seconds = double.Parse(Arg("-capSeconds", "40"), System.Globalization.CultureInfo.InvariantCulture);
            double step = double.Parse(Arg("-capStep", "0.5"), System.Globalization.CultureInfo.InvariantCulture);
            EditorSceneManager.OpenScene("Assets/FluidStabilizer/Scenes/PlayScene.unity");
            var comparison = Object.FindAnyObjectByType<ComparisonRunner>();
            Directory.CreateDirectory("Logs/gif");
            comparison.StartComparison(DriverTrace.LoadFromFile(tracePath), true,
                new[] { StabilizerChoice.Off, StabilizerChoice.SpecificForceAlignmentPlusI });

            const int w = 640, h = 360;
            var csv = new System.Text.StringBuilder("frame,t,spill1,spill2\n");
            int frames = (int)(seconds / step);
            for (int f = 0; f < frames; f++)
            {
                comparison.PumpForCapture(step);
                for (int lane = 0; lane < comparison.LaneCount; lane++)
                {
                    var cam = comparison.LaneCamera(lane);
                    var oldRect = cam.rect;
                    cam.rect = new Rect(0, 0, 1, 1);
                    cam.aspect = w / (float)h;
                    cam.fieldOfView = float.Parse(Arg("-capFov", "24"), System.Globalization.CultureInfo.InvariantCulture);
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.55f, 0.62f, 0.72f);
                    var rt = new RenderTexture(w, h, 24);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    tex.Apply();
                    File.WriteAllBytes($"Logs/gif/f{f:D3}-{lane}.png", tex.EncodeToPNG());
                    cam.targetTexture = null;
                    RenderTexture.active = null;
                    cam.rect = oldRect;
                    Object.DestroyImmediate(rt);
                    Object.DestroyImmediate(tex);
                }
                csv.Append($"{f},{(f + 1) * step:F1},{comparison.LaneSpillLitres(0):F2},{comparison.LaneSpillLitres(1):F2}\n");
            }
            File.WriteAllText("Logs/gif/spill.csv", csv.ToString());
            comparison.Stop();
            Debug.Log($"[SceneCapture] wrote {frames} frames to Logs/gif/");
        }

        private static string Arg(string name, string fallback)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return fallback;
        }

        private static DriverTrace RecordRoadTrace(ulong seed, double seconds)
        {
            var cfg = new FluidStabilizer.Core.Sim.SimulationConfig { EpisodeSeconds = seconds };
            cfg.Slosh.FillRatio = 0.64;
            cfg.Road.Enabled = true;
            var trace = new DriverTrace
            {
                Label = "capture-road", Seed = seed, StepSeconds = cfg.PhysicsStepSeconds,
                FillRatio = 0.64, RoadEnabled = true
            };
            var loop = new FluidStabilizer.Core.Sim.SimulationLoop(cfg,
                new FluidStabilizer.Controller.HoldController(),
                new RecordingDriver(new HighwayDriver(seed, briskness: 0.5), trace),
                new FluidStabilizer.Core.Sim.SloshModels.PendulumSloshModel());
            loop.BeginEpisode(seed);
            while (loop.StepPhysics()) { }
            return trace;
        }

        private static void ChaseCam(Camera cam, Transform t, float yaw, float up = 3.6f, float back = 8f)
        {
            Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * (-t.forward);
            cam.transform.position = t.position + dir * back + Vector3.up * up;
            cam.transform.rotation = Quaternion.LookRotation(t.position + Vector3.up * 1.6f - cam.transform.position);
        }

        private static void Write(Camera cam, string tag)
        {
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
