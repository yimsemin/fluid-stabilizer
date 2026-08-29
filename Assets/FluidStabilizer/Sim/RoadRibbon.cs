using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Sim;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Draws the road the truck is driving on: a multi-lane carriageway with
    /// shoulders that follows the road's curve, tilts with grade and bank, and
    /// dips into potholes, scrolling toward you as the truck advances. The truck
    /// sits at its actual lane position within it. Drawing the shoulders means
    /// the ribbon <em>is</em> the ground near the truck — no separate plane to
    /// clip through.
    ///
    /// Purely cosmetic. Rebuilds an independent <see cref="RoadModel"/> from the
    /// loop's seed and config, so it reproduces the profile the physics feels
    /// without touching it.
    /// </summary>
    public sealed class RoadRibbon : MonoBehaviour
    {
        public SimulationRunner Runner;
        public SimulationLoop LoopOverride;
        public Vector3 WorldOffset = Vector3.zero;
        public bool ExternallyDriven;

        [Header("Extent")]
        public float AheadMetres = 27f;
        public float BehindMetres = 7f;
        public float SampleSpacingMetres = 0.4f;
        public float ShoulderMetres = 12f;
        public float VerticalExaggeration = 3.0f;
        public float TileMetres = 6f;

        private RoadModel _road;
        private ulong _roadSeed = ulong.MaxValue;

        private Transform _strip;
        private Mesh _mesh;
        private Vector3[] _verts;
        private Color[] _colors;
        private Vector2[] _uv;
        private int _nz;

        // cross-section: dense on the carriageway (lane-aligned, exact vertices at
        // the edges), sparse on the shoulders. Built once for a given road width.
        private float[] _crossX;
        private bool[] _crossOnRoad;
        private float _crossRoadHalf = -1f;

        private SimulationLoop Loop => LoopOverride ?? Runner?.Loop;

        private void LateUpdate() { if (!ExternallyDriven) RenderTick(); }

        public void RenderTick()
        {
            var loop = Loop;
            if (loop == null) return;
            var cfg = loop.Config;

            EnsureStrip(cfg);
            _strip.gameObject.SetActive(true);
            bool on = cfg.Road.Enabled;

            if (_road == null || loop.Seed != _roadSeed)
            {
                _road = new RoadModel(cfg.Road);
                _road.Reset(new DeterministicRandom(loop.Seed).Fork("road"));
                _roadSeed = loop.Seed;
            }

            var v = loop.VehicleState;
            Vector3 fwd = Frames.ToUnity(v.OrientationWorldFromBody * new Double3(1, 0, 0));
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;

            Vector3 basePos = Frames.ToUnity(v.OriginWorld);
            basePos.y = 0.02f;
            _strip.SetPositionAndRotation(basePos + WorldOffset, Quaternion.LookRotation(fwd.normalized, Vector3.up));

            Rebuild(loop.DistanceTraveled, on ? (float)v.LateralOffsetFromRoadCentre : 0f, on);
        }

        private void Rebuild(double sTruck, float roadCentreLocalX, bool markings)
        {
            int nz = _nz, nx = _crossX.Length;
            float dz = SampleSpacingMetres;
            int truckI = Mathf.Clamp(Mathf.RoundToInt(BehindMetres / dz), 0, nz - 1);

            // Longitudinal sweep: integrate curvature into a centreline. (Grade is
            // not shown as road elevation — the truck doesn't actually climb, and
            // a runaway rise/fall at the ribbon's far end reads as a tear.)
            var rx = new float[nz]; var rz = new float[nz]; var rphi = new float[nz];
            var surf = new float[nz]; var bankTan = new float[nz];
            double phi = 0, x = 0, z = 0;
            for (int i = 0; i < nz; i++)
            {
                double s = sTruck - BehindMetres + i * dz;
                RoadInput r = _road.Sample(s);
                if (i > 0)
                {
                    phi += r.Curvature * dz;
                    x += -System.Math.Sin(phi) * dz;
                    z += System.Math.Cos(phi) * dz;
                }
                rx[i] = (float)x; rz[i] = (float)z; rphi[i] = (float)phi;
                surf[i] = (float)r.SurfaceHeight; bankTan[i] = (float)System.Math.Tan(r.Bank);
            }

            // Align station truckI with the truck (origin, heading +Z).
            float rot = -rphi[truckI];
            float cosR = Mathf.Cos(rot), sinR = Mathf.Sin(rot);
            float px = rx[truckI], pz = rz[truckI];

            for (int i = 0; i < nz; i++)
            {
                float dx = rx[i] - px, dzz = rz[i] - pz;
                float clx = roadCentreLocalX + (dx * cosR - dzz * sinR);
                float clz = dx * sinR + dzz * cosR;
                float lphi = rphi[i] + rot;
                float rX = Mathf.Cos(lphi), rZ = Mathf.Sin(lphi);

                float baseY = Mathf.Max(-0.28f, surf[i] * VerticalExaggeration);
                float shade = Mathf.Clamp(1f + surf[i] * 16f, 0.3f, 1.7f);
                float vTex = (float)((sTruck - BehindMetres + i * dz) / TileMetres);

                for (int j = 0; j < nx; j++)
                {
                    float localX = _crossX[j];
                    bool onRoad = markings && _crossOnRoad[j];
                    float bankX = Mathf.Clamp(localX, -_crossRoadHalf, _crossRoadHalf);

                    // U stays in / just past the road band near the carriageway so
                    // the road→verge transition quad never samples the whole
                    // texture width (which would draw a squished ghost road).
                    float uTex = onRoad ? 0.5f + 0.449f * (localX / _crossRoadHalf)
                                        : (localX < 0f ? 0.02f : 0.98f);

                    int k = i * nx + j;
                    _verts[k] = new Vector3(clx + rX * localX, baseY - bankX * bankTan[i], clz + rZ * localX);
                    _uv[k] = new Vector2(uTex, vTex);
                    _colors[k] = onRoad
                        ? new Color(shade, shade, shade)
                        : (markings ? new Color(1.15f, 1.08f, 0.85f) : new Color(0.95f, 0.95f, 1f));
                }
            }

            _mesh.vertices = _verts;
            _mesh.uv = _uv;
            _mesh.colors = _colors;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }

        private void EnsureStrip(SimulationConfig cfg)
        {
            float roadHalf = (float)cfg.Road.RoadHalfWidthM;
            if (_strip != null && Mathf.Approximately(roadHalf, _crossRoadHalf)) return;

            BuildCrossSection(roadHalf, (float)cfg.Road.LaneWidthM);
            _nz = Mathf.Max(8, Mathf.CeilToInt((AheadMetres + BehindMetres) / SampleSpacingMetres) + 1);
            int nx = _crossX.Length, n = _nz * nx;

            if (_strip == null)
            {
                var go = new GameObject("RoadRibbon");
                go.transform.SetParent(transform, worldPositionStays: false);
                _strip = go.transform;
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var sh = Shader.Find("FluidStabilizer/RoadRibbon");
                mr.sharedMaterial = new Material(sh != null ? sh : Shader.Find("Sprites/Default")) { name = "RoadRibbon" };
                mr.sharedMaterial.mainTexture = BuildRoadTexture();
                _mesh = new Mesh { name = "RoadRibbon", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                _mesh.MarkDynamic();
                mf.sharedMesh = _mesh;
            }

            _verts = new Vector3[n];
            _colors = new Color[n];
            _uv = new Vector2[n];

            var tris = new System.Collections.Generic.List<int>((_nz - 1) * (nx - 1) * 6);
            for (int i = 0; i < _nz - 1; i++)
            for (int j = 0; j < nx - 1; j++)
            {
                int a = i * nx + j, b = i * nx + j + 1, c = (i + 1) * nx + j, d = (i + 1) * nx + j + 1;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
            _mesh.Clear();
            _mesh.vertices = _verts;
            _mesh.uv = _uv;
            _mesh.triangles = tris.ToArray();
        }

        private void BuildCrossSection(float roadHalf, float laneW)
        {
            var xs = new System.Collections.Generic.List<float>();
            var road = new System.Collections.Generic.List<bool>();

            xs.Add(-(roadHalf + ShoulderMetres)); road.Add(false);
            xs.Add(-(roadHalf + 0.5f * ShoulderMetres)); road.Add(false);
            // carriageway: a vertex on every half-lane, exact vertices at the edges
            int steps = Mathf.Max(2, Mathf.RoundToInt(2f * roadHalf / (0.5f * laneW)));
            for (int i = 0; i <= steps; i++)
            {
                float xv = Mathf.Lerp(-roadHalf, roadHalf, i / (float)steps);
                xs.Add(xv); road.Add(true);
            }
            xs.Add(roadHalf + 0.5f * ShoulderMetres); road.Add(false);
            xs.Add(roadHalf + ShoulderMetres); road.Add(false);

            _crossX = xs.ToArray();
            _crossOnRoad = road.ToArray();
            _crossRoadHalf = roadHalf;
        }

        /// <summary>Asphalt band in U∈[0.05,0.95] with edge lines, a double-yellow
        /// centre and dashed lane dividers; shoulder swatch outside.</summary>
        private static Texture2D BuildRoadTexture()
        {
            const int w = 128, h = 128;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapModeU = TextureWrapMode.Clamp, wrapModeV = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear, name = "RoadTex",
            };
            var rng = new System.Random(12345);
            var px = new Color32[w * h];
            var white = new Color(0.80f, 0.80f, 0.76f);
            var yellow = new Color(0.82f, 0.66f, 0.16f);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)(w - 1);
                float n = (float)(rng.NextDouble() - 0.5) * 0.05f;
                Color c;
                if (u < 0.05f || u > 0.95f)
                {
                    c = new Color(0.40f + n, 0.38f + n, 0.30f + n);        // shoulder
                }
                else
                {
                    float r = (u - 0.5f) / 0.45f;                          // -1..1 across the carriageway
                    c = new Color(0.11f + n, 0.11f + n, 0.12f + n);
                    bool dash = y < h * 0.5f;
                    if (Mathf.Abs(r) > 0.95f) c = white;
                    else if (Mathf.Abs(r) < 0.03f || (Mathf.Abs(r) > 0.05f && Mathf.Abs(r) < 0.085f)) c = yellow;
                    else if (dash && Mathf.Abs(Mathf.Abs(r) - 0.5f) < 0.028f) c = white;
                }
                px[y * w + x] = c;
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        /// <summary>Hide the drawn strip now (OnDisable isn't reliable outside play mode).</summary>
        public void SetStripVisible(bool visible) { if (_strip != null) _strip.gameObject.SetActive(visible); }

        private void OnDisable() { SetStripVisible(false); }
        private void OnDestroy() { if (_strip != null) Destroy(_strip.gameObject); }

        public Mesh DebugMesh => _mesh;
        public Transform DebugStrip => _strip;
    }
}
