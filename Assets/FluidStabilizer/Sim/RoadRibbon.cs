using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Sim;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Draws the road the truck is driving on — a strip that scrolls toward you as
    /// the truck advances, tilting with grade and bank and dipping into potholes.
    /// It makes the disturbance visible: you see the bump arrive, then see the
    /// tank react (experiment-plan.md step 2b).
    ///
    /// Purely cosmetic. It rebuilds an independent <see cref="RoadModel"/> from the
    /// loop's seed and config, so it reproduces the same profile the physics sees
    /// without touching it. Hidden when the road is off.
    /// </summary>
    public sealed class RoadRibbon : MonoBehaviour
    {
        public SimulationRunner Runner;
        public SimulationLoop LoopOverride;
        public Vector3 WorldOffset = Vector3.zero;

        [Header("Extent")]
        public float AheadMetres = 46f;
        public float BehindMetres = 12f;
        public int LongSamples = 72;
        public float HalfWidthMetres = 3.0f;
        public int CrossSamples = 7;

        private RoadModel _road;
        private ulong _roadSeed = ulong.MaxValue;
        private bool _roadEnabled;

        private Transform _strip;
        private Mesh _mesh;
        private Vector3[] _verts;
        private Color[] _colors;

        private SimulationLoop Loop => LoopOverride ?? Runner?.Loop;

        private void LateUpdate() => RenderTick();

        public void RenderTick()
        {
            var loop = Loop;
            if (loop == null) return;

            var cfg = loop.Config;
            if (!cfg.Road.Enabled)
            {
                if (_strip != null) _strip.gameObject.SetActive(false);
                return;
            }

            EnsureStrip();
            _strip.gameObject.SetActive(true);

            // (Re)build the road replica if the episode changed.
            if (_road == null || loop.Seed != _roadSeed || _roadEnabled != cfg.Road.Enabled)
            {
                _road = new RoadModel(cfg.Road);
                _road.Reset(new DeterministicRandom(loop.Seed).Fork("road"));
                _roadSeed = loop.Seed;
                _roadEnabled = cfg.Road.Enabled;
            }

            var v = loop.VehicleState;
            Vector3 worldFwd = Frames.ToUnity(v.OrientationWorldFromBody * new Double3(1, 0, 0));
            worldFwd.y = 0f;
            if (worldFwd.sqrMagnitude < 1e-6f) worldFwd = Vector3.forward;

            Vector3 basePos = Frames.ToUnity(v.OriginWorld);
            basePos.y = 0.05f; // clear of the ground plane (avoids z-fighting)
            _strip.SetPositionAndRotation(basePos + WorldOffset, Quaternion.LookRotation(worldFwd.normalized, Vector3.up));

            Rebuild(loop.DistanceTraveled);
        }

        private void Rebuild(double distanceAtTruck)
        {
            int nz = LongSamples, nx = CrossSamples;
            float total = AheadMetres + BehindMetres;
            float dz = total / (nz - 1);

            // Walk s forward, accumulating the grade slope into a height profile.
            double gradeY = 0.0;
            var profileY = new float[nz];
            var surf = new float[nz];
            var bank = new float[nz];
            double prevGrade = 0.0;
            for (int i = 0; i < nz; i++)
            {
                double s = distanceAtTruck - BehindMetres + i * dz;
                RoadInput r = _road.Sample(s);
                // uphill grade (positive) → the road ahead rises in +Y.
                if (i > 0) gradeY += System.Math.Tan(0.5 * (r.Grade + prevGrade)) * dz;
                prevGrade = r.Grade;
                profileY[i] = (float)gradeY;
                surf[i] = (float)r.SurfaceHeight;
                bank[i] = (float)r.Bank;
            }

            // Zero the profile at the truck (localZ = 0 → i where s = distanceAtTruck).
            int truckI = Mathf.Clamp(Mathf.RoundToInt(BehindMetres / dz), 0, nz - 1);
            float y0 = profileY[truckI] + surf[truckI];
            var asphalt = new Color(0.12f, 0.12f, 0.14f);
            var paint = new Color(0.80f, 0.74f, 0.42f);
            var edge = new Color(0.62f, 0.62f, 0.66f);

            for (int i = 0; i < nz; i++)
            {
                double s = distanceAtTruck - BehindMetres + i * dz;
                bool dashOn = ((int)System.Math.Floor(s / 3.0) & 1) == 0;
                float localZ = -BehindMetres + i * dz;
                float bankTan = (float)System.Math.Tan(bank[i]);
                float shade = Mathf.Clamp(1f + surf[i] * 5f, 0.55f, 1.5f);

                for (int j = 0; j < nx; j++)
                {
                    float localX = Mathf.Lerp(-HalfWidthMetres, HalfWidthMetres, j / (float)(nx - 1));
                    float y = profileY[i] - y0 + surf[i] - localX * bankTan;
                    int k = i * nx + j;
                    _verts[k] = new Vector3(localX, y, localZ);

                    Color c = asphalt;
                    if (Mathf.Abs(localX) > HalfWidthMetres - 0.28f) c = edge;
                    else if (Mathf.Abs(localX) < 0.16f && dashOn) c = paint;
                    _colors[k] = c * shade;
                }
            }

            _mesh.vertices = _verts;
            _mesh.colors = _colors;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }

        private void EnsureStrip()
        {
            if (_strip != null) return;

            var go = new GameObject("RoadRibbon");
            go.transform.SetParent(transform, worldPositionStays: false);
            _strip = go.transform;

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var sh = Shader.Find("Sprites/Default");
            mr.sharedMaterial = sh != null
                ? new Material(sh) { name = "RoadRibbon" }
                : new Material(Shader.Find("Standard")) { name = "RoadRibbon", color = new Color(0.13f, 0.13f, 0.15f) };

            int nz = LongSamples, nx = CrossSamples, n = nz * nx;
            _verts = new Vector3[n];
            _colors = new Color[n];

            var tris = new System.Collections.Generic.List<int>((nz - 1) * (nx - 1) * 6);
            for (int i = 0; i < nz - 1; i++)
            for (int j = 0; j < nx - 1; j++)
            {
                int a = i * nx + j, b = i * nx + j + 1, c = (i + 1) * nx + j, d = (i + 1) * nx + j + 1;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }

            _mesh = new Mesh { name = "RoadRibbon" };
            _mesh.MarkDynamic();
            _mesh.vertices = _verts;
            _mesh.triangles = tris.ToArray();
            mf.sharedMesh = _mesh;
        }

        private void OnDisable()
        {
            if (_strip != null) _strip.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_strip != null) Destroy(_strip.gameObject);
        }

        // test aids
        public Mesh DebugMesh => _mesh;
        public Transform DebugStrip => _strip;
    }
}
