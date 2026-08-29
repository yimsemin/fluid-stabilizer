using System.IO;
using FluidStabilizer.Sim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// Builds the interactive play scene from code and saves it, so the scene is
    /// reproducible and version-controlled rather than hand-wired.
    ///
    /// Regenerate:  Menu ▸ FluidStabilizer ▸ Rebuild Play Scene
    /// or headless:  Unity -batchmode -quit -executeMethod
    ///               FluidStabilizer.SimEditor.SceneBuilder.Build
    /// </summary>
    public static class SceneBuilder
    {
        private const string ScenePath = "Assets/FluidStabilizer/Scenes/PlayScene.unity";

        [MenuItem("FluidStabilizer/Rebuild Play Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // --- environment ---
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(2000, 1, 2000);
            // A distant backdrop only — RoadRibbon draws the road + shoulders as
            // one continuous surface near the truck, so nothing clips into this.
            ground.transform.position = new Vector3(0, -1.5f, 0);
            ground.GetComponent<Renderer>().sharedMaterial = SolidMaterial("Ground", new Color(0.16f, 0.18f, 0.14f));

            var lightGo = new GameObject("Sun");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50, -35, 0);
            RenderSettings.ambientLight = new Color(0.35f, 0.37f, 0.4f);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.transform.position = new Vector3(0, 5, -10);
            cam.transform.rotation = Quaternion.Euler(15, 0, 0);
            camGo.AddComponent<AudioListener>();

            // --- truck / tank rig ---
            // "Rig" is the unscaled root the view moves as the truck body. The
            // truck is a low flatbed so the tank + liquid sit clearly ABOVE it,
            // not buried in the body — the tank is lifted a cosmetic amount by
            // SimView (SimView.TankVisualLift) purely so a viewer can see the
            // surface tilt and any overflow. Physics is unchanged.
            var rig = new GameObject("TruckRig");

            var chassis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            chassis.name = "Chassis";
            chassis.transform.SetParent(rig.transform, false);
            chassis.transform.localScale = new Vector3(2.4f, 0.9f, 6.0f);
            chassis.transform.localPosition = new Vector3(0, 0.45f, 2.0f); // origin = rear axle at ground
            chassis.GetComponent<Renderer>().sharedMaterial =
                SolidMaterial("Truck", new Color(0.28f, 0.38f, 0.6f));
            Object.DestroyImmediate(chassis.GetComponent<BoxCollider>());

            var cab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cab.name = "Cab";
            cab.transform.SetParent(rig.transform, false);
            cab.transform.localScale = new Vector3(2.3f, 1.5f, 1.6f);
            cab.transform.localPosition = new Vector3(0, 1.15f, 4.4f);
            cab.GetComponent<Renderer>().sharedMaterial =
                SolidMaterial("Cab", new Color(0.22f, 0.30f, 0.5f));
            Object.DestroyImmediate(cab.GetComponent<BoxCollider>());

            var pivot = new GameObject("TankPivot");
            pivot.transform.SetParent(rig.transform, false);

            var mount = GameObject.CreatePrimitive(PrimitiveType.Cube);
            mount.name = "Mount";
            mount.transform.SetParent(pivot.transform, false);
            mount.transform.localScale = new Vector3(0.5f, 0.6f, 0.5f);
            mount.transform.localPosition = new Vector3(0, -0.55f, 0); // links the bed up to the tank
            mount.GetComponent<Renderer>().sharedMaterial = SolidMaterial("Mount", new Color(0.15f, 0.16f, 0.18f));
            Object.DestroyImmediate(mount.GetComponent<BoxCollider>());

            var tank = new GameObject("TankBody");
            tank.transform.SetParent(pivot.transform, false);

            // The tank: an open edge frame (12 beams, no panels) so you can see
            // straight in. Unit cube in [-0.5,0.5]^3; SimView scales it (w,h,l).
            var shell = new GameObject("Shell", typeof(MeshFilter), typeof(MeshRenderer));
            shell.transform.SetParent(tank.transform, false);
            shell.GetComponent<MeshFilter>().sharedMesh = TankFrameMesh();
            var shellMr = shell.GetComponent<MeshRenderer>();
            shellMr.sharedMaterial = SolidMaterial("TankFrame", new Color(0.80f, 0.85f, 0.90f));
            shellMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // The liquid: a closed, deforming water body (SimView builds/updates
            // the mesh each frame from the active slosh model's free surface).
            var liquidMeshGo = new GameObject("LiquidMesh", typeof(MeshFilter), typeof(MeshRenderer));
            liquidMeshGo.transform.SetParent(tank.transform, false);
            var liquidMeshMr = liquidMeshGo.GetComponent<MeshRenderer>();
            liquidMeshMr.sharedMaterial = WaterMaterial();
            liquidMeshMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // --- controllers ---
            var simGo = new GameObject("Simulation");
            var runner = simGo.AddComponent<SimulationRunner>();
            var view = simGo.AddComponent<SimView>();
            var hud = simGo.AddComponent<Hud>();
            var controls = simGo.AddComponent<PlayControls>();
            var traceRec = simGo.AddComponent<TraceRecorder>();
            var comparison = simGo.AddComponent<ComparisonRunner>();
            var roadRibbon = simGo.AddComponent<RoadRibbon>();

            view.Runner = runner;
            view.TruckBody = rig.transform;
            view.TankPivot = pivot.transform;
            view.TankBody = tank.transform;
            view.LiquidMesh = liquidMeshGo.GetComponent<MeshFilter>();

            hud.Runner = runner;

            controls.Runner = runner;
            controls.View = view;
            controls.Camera = cam;
            controls.Comparison = comparison;

            traceRec.Runner = runner;

            comparison.Runner = runner;
            comparison.RigTemplate = rig.transform;
            comparison.Camera = cam;

            roadRibbon.Runner = runner;

            // --- save ---
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);

            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!list.Exists(s => s.path == ScenePath))
                list.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();

            Debug.Log($"[SceneBuilder] wrote {ScenePath}");
        }

        private static Material SolidMaterial(string name, Color c)
        {
            var m = new Material(Shader.Find("Standard")) { name = name, color = c };
            return m;
        }

        private static Material TransparentMaterial(string name, Color c)
        {
            var m = new Material(Shader.Find("Standard")) { name = name };
            m.SetFloat("_Mode", 3); // Transparent
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = 3000;
            m.color = c;
            return m;
        }

        private static Material WaterMaterial()
        {
            var s = Shader.Find("FluidStabilizer/WaterSurface");
            if (s != null)
            {
                var m = new Material(s) { name = "Water" };
                m.SetFloat("_BaseAlpha", 0.72f);
                m.SetColor("_DeepColor", new Color(0.05f, 0.24f, 0.42f));
                m.SetColor("_ShallowColor", new Color(0.20f, 0.62f, 0.72f));
                m.SetColor("_EdgeColor", new Color(0.82f, 0.95f, 1f));
                return m;
            }
            return TransparentMaterial("Water", new Color(0.10f, 0.45f, 0.75f, 0.82f));
        }

        /// <summary>
        /// Unit-cube edge frame (12 beams, [-0.5,0.5]^3) — the open tank drawn as
        /// bars only so the liquid inside is fully visible. Scaled to (w,h,l) by
        /// SimView; the top 4 beams mark the rim.
        /// </summary>
        private static Mesh TankFrameMesh()
        {
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();
            const float r = 0.018f; // beam half-thickness (unit-cube fraction)

            void Box(Vector3 c, Vector3 h)
            {
                Vector3[] p =
                {
                    c + new Vector3(-h.x,-h.y,-h.z), c + new Vector3(h.x,-h.y,-h.z),
                    c + new Vector3(h.x,-h.y,h.z),   c + new Vector3(-h.x,-h.y,h.z),
                    c + new Vector3(-h.x,h.y,-h.z),  c + new Vector3(h.x,h.y,-h.z),
                    c + new Vector3(h.x,h.y,h.z),    c + new Vector3(-h.x,h.y,h.z),
                };
                int b = v.Count;
                foreach (var q in p) v.Add(q);
                int[] f = { 0,1,2, 0,2,3,  4,6,5, 4,7,6,  0,5,1, 0,4,5,
                            1,6,2, 1,5,6,  2,7,3, 2,6,7,  3,4,0, 3,7,4 };
                foreach (var idx in f) t.Add(b + idx);
            }

            const float e = 0.5f;
            foreach (float sx in new[] { -e, e })
            foreach (float sz in new[] { -e, e })
                Box(new Vector3(sx, 0, sz), new Vector3(r, e, r));          // 4 verticals
            foreach (float sy in new[] { -e, e })
            {
                foreach (float sz in new[] { -e, e }) Box(new Vector3(0, sy, sz), new Vector3(e, r, r));
                foreach (float sx in new[] { -e, e }) Box(new Vector3(sx, sy, 0), new Vector3(r, r, e));
            }

            var m = new Mesh { name = "TankFrame" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
