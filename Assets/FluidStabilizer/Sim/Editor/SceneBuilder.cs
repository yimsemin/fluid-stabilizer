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
            ground.GetComponent<Renderer>().sharedMaterial = SolidMaterial("Ground", new Color(0.22f, 0.24f, 0.26f));

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
            // "Rig" is the unscaled root the view moves as the truck body; the
            // visible truck mesh is a scaled child so its scale never distorts
            // the tank geometry hung off the (also unscaled) pivot.
            var rig = new GameObject("TruckRig");

            var truckMesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
            truckMesh.name = "TruckMesh";
            truckMesh.transform.SetParent(rig.transform, false);
            truckMesh.transform.localScale = new Vector3(2.4f, 1.2f, 6.0f);
            truckMesh.transform.localPosition = new Vector3(0, 0.9f, 2.2f); // origin = rear axle at ground
            truckMesh.GetComponent<Renderer>().sharedMaterial =
                SolidMaterial("Truck", new Color(0.30f, 0.45f, 0.75f));
            Object.DestroyImmediate(truckMesh.GetComponent<BoxCollider>());

            var pivot = new GameObject("TankPivot");
            pivot.transform.SetParent(rig.transform, false);

            var tank = new GameObject("TankBody");
            tank.transform.SetParent(pivot.transform, false);

            // Open tank: floor + 4 walls, no lid (an open liquid tank). Centred
            // on the tank body origin; SimView scales it from the config.
            var shell = new GameObject("Shell", typeof(MeshFilter), typeof(MeshRenderer));
            shell.transform.SetParent(tank.transform, false);
            shell.GetComponent<MeshFilter>().sharedMesh = OpenBoxMesh();
            var shellMr = shell.GetComponent<MeshRenderer>();
            shellMr.sharedMaterial = TransparentMaterial("TankShell", new Color(0.82f, 0.86f, 0.9f, 0.14f));
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

            view.Runner = runner;
            view.TruckBody = rig.transform;
            view.TankPivot = pivot.transform;
            view.TankBody = tank.transform;
            view.LiquidMesh = liquidMeshGo.GetComponent<MeshFilter>();

            hud.Runner = runner;

            controls.Runner = runner;
            controls.View = view;
            controls.Camera = cam;

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
            if (s != null) return new Material(s) { name = "Water" };
            // fallback if the shader failed to import
            return TransparentMaterial("Water", new Color(0.15f, 0.5f, 0.85f, 0.7f));
        }

        /// <summary>Unit open box: floor + 4 walls (no lid), scaled by SimView.</summary>
        private static Mesh OpenBoxMesh()
        {
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
                t.Add(i); t.Add(i + 2); t.Add(i + 3);
                // back faces so the walls are visible from inside too
                t.Add(i); t.Add(i + 2); t.Add(i + 1);
                t.Add(i); t.Add(i + 3); t.Add(i + 2);
            }

            const float e = 0.5f;
            Quad(new Vector3(-e, -e, -e), new Vector3(e, -e, -e), new Vector3(e, -e, e), new Vector3(-e, -e, e)); // floor
            Quad(new Vector3(-e, -e, -e), new Vector3(-e, e, -e), new Vector3(e, e, -e), new Vector3(e, -e, -e)); // -z
            Quad(new Vector3(e, -e, -e), new Vector3(e, e, -e), new Vector3(e, e, e), new Vector3(e, -e, e));     // +x
            Quad(new Vector3(e, -e, e), new Vector3(e, e, e), new Vector3(-e, e, e), new Vector3(-e, -e, e));     // +z
            Quad(new Vector3(-e, -e, e), new Vector3(-e, e, e), new Vector3(-e, e, -e), new Vector3(-e, -e, -e)); // -x

            var m = new Mesh { name = "OpenTankShell" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
