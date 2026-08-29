using FluidStabilizer.Contracts;
using FluidStabilizer.Core;
using FluidStabilizer.Core.Sim;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Draws the road the truck is driving on — a strip that follows the road's
    /// curve, tilts with grade and bank, and dips into potholes, scrolling toward
    /// you as the truck advances. It makes the disturbance visible: you see the
    /// bend and the bump arrive, then see the tank react (experiment-plan.md 2b).
    ///
    /// Purely cosmetic. Rebuilds an independent <see cref="RoadModel"/> from the
    /// loop's seed and config, so it reproduces the same profile the physics sees
    /// without touching it. Hidden when the road is off.
    /// </summary>
    public sealed class RoadRibbon : MonoBehaviour
    {
        public SimulationRunner Runner;
        public SimulationLoop LoopOverride;
        public Vector3 WorldOffset = Vector3.zero;
        public bool ExternallyDriven;

        [Header("Extent")]
        public float AheadMetres = 42f;
        public float BehindMetres = 10f;
        public float SampleSpacingMetres = 0.4f;
        public float HalfWidthMetres = 2.3f;
        public float VerticalExaggeration = 7.0f;
        public float TileMetres = 6f;           // one dash + gap of the texture

        private RoadModel _road;
        private ulong _roadSeed = ulong.MaxValue;

        private Transform _strip;
        private Mesh _mesh;
        private Vector3[] _verts;
        private Color[] _colors;
        private Vector2[] _uv;
        private const int Nx = 7;               // cross samples (odd → a centre column)
        private int _nz;

        private SimulationLoop Loop => LoopOverride ?? Runner?.Loop;

        private void LateUpdate() { if (!ExternallyDriven) RenderTick(); }

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

            if (_road == null || loop.Seed != _roadSeed)
            {
                _road = new RoadModel(cfg.Road);
                _road.Reset(new DeterministicRandom(loop.Seed).Fork("road"));
                _roadSeed = loop.Seed;
            }

            var v = loop.VehicleState;
            Vector3 worldFwd = Frames.ToUnity(v.OrientationWorldFromBody * new Double3(1, 0, 0));
            worldFwd.y = 0f;
            if (worldFwd.sqrMagnitude < 1e-6f) worldFwd = Vector3.forward;

            Vector3 basePos = Frames.ToUnity(v.OriginWorld);
            basePos.y = 0.05f; // just above the ground plane (which SceneBuilder drops to -0.04)
            _strip.SetPositionAndRotation(basePos + WorldOffset,
                Quaternion.LookRotation(worldFwd.normalized, Vector3.up));

            Rebuild(loop.DistanceTraveled);
        }

        private void Rebuild(double sTruck)
        {
            int nz = _nz;
            float dz = SampleSpacingMetres;
            int truckI = Mathf.Clamp(Mathf.RoundToInt(BehindMetres / dz), 0, nz - 1);

            // Sample the road, and integrate curvature into a centreline path in
            // the strip's local frame (+Z forward at the truck, heading φ = 0).
            var cx = new float[nz];
            var cz = new float[nz];
            var cphi = new float[nz];
            var gradeY = new float[nz];
            var surf = new float[nz];
            var bankTan = new float[nz];

            double phi = 0, x = 0, z = 0, gy = 0;
            double prevGrade = 0;
            for (int i = 0; i < nz; i++)
            {
                double s = sTruck - BehindMetres + i * dz;
                RoadInput r = _road.Sample(s);

                if (i > 0)
                {
                    phi += r.Curvature * dz;                     // dφ/ds = curvature
                    x += -System.Math.Sin(phi) * dz;
                    z += System.Math.Cos(phi) * dz;
                    gy += System.Math.Tan(0.5 * (r.Grade + prevGrade)) * dz;
                }
                prevGrade = r.Grade;

                cx[i] = (float)x; cz[i] = (float)z; cphi[i] = (float)phi;
                gradeY[i] = (float)gy;
                surf[i] = (float)r.SurfaceHeight;
                bankTan[i] = (float)System.Math.Tan(r.Bank);
            }

            // Zero the frame at the truck station.
            float ox = cx[truckI], oz = cz[truckI], ophi = cphi[truckI];
            float oy = gradeY[truckI] + surf[truckI] * VerticalExaggeration;
            float cosO = Mathf.Cos(-ophi), sinO = Mathf.Sin(-ophi);

            var asphalt = new Color(1f, 1f, 1f);
            for (int i = 0; i < nz; i++)
            {
                // centreline in truck-local frame (rotate so the truck heads +Z)
                float rx = cx[i] - ox, rz = cz[i] - oz;
                float lx = rx * cosO - rz * sinO;
                float lz = rx * sinO + rz * cosO;
                float lphi = cphi[i] - ophi;

                float dip = surf[i] * VerticalExaggeration;
                float y = gradeY[i] + dip - oy;

                // pothole → dark, bump → light
                float shade = Mathf.Clamp(1f + surf[i] * 16f, 0.3f, 1.7f);
                Color c = asphalt * shade;

                float rightX = Mathf.Cos(lphi), rightZ = Mathf.Sin(lphi);
                float vTex = (float)((sTruck - BehindMetres + i * dz) / TileMetres);

                for (int j = 0; j < Nx; j++)
                {
                    float u = j / (float)(Nx - 1);
                    float localX = Mathf.Lerp(-HalfWidthMetres, HalfWidthMetres, u);
                    int k = i * Nx + j;
                    _verts[k] = new Vector3(
                        lx + rightX * localX,
                        y - localX * bankTan[i],
                        lz + rightZ * localX);
                    _colors[k] = c;
                    _uv[k] = new Vector2(u, vTex);
                }
            }

            _mesh.vertices = _verts;
            _mesh.uv = _uv;
            _mesh.colors = _colors;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }

        private void EnsureStrip()
        {
            if (_strip != null) return;

            _nz = Mathf.Max(8, Mathf.CeilToInt((AheadMetres + BehindMetres) / SampleSpacingMetres) + 1);
            int n = _nz * Nx;

            var go = new GameObject("RoadRibbon");
            go.transform.SetParent(transform, worldPositionStays: false);
            _strip = go.transform;

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var sh = Shader.Find("FluidStabilizer/RoadRibbon");
            mr.sharedMaterial = new Material(sh != null ? sh : Shader.Find("Sprites/Default")) { name = "RoadRibbon" };
            mr.sharedMaterial.mainTexture = BuildRoadTexture();

            _verts = new Vector3[n];
            _colors = new Color[n];
            _uv = new Vector2[n];

            var tris = new System.Collections.Generic.List<int>((_nz - 1) * (Nx - 1) * 6);
            for (int i = 0; i < _nz - 1; i++)
            for (int j = 0; j < Nx - 1; j++)
            {
                int a = i * Nx + j, b = i * Nx + j + 1, cc = (i + 1) * Nx + j, d = (i + 1) * Nx + j + 1;
                tris.Add(a); tris.Add(cc); tris.Add(b);
                tris.Add(b); tris.Add(cc); tris.Add(d);
            }

            _mesh = new Mesh { name = "RoadRibbon" };
            _mesh.MarkDynamic();
            _mesh.vertices = _verts;
            _mesh.uv = _uv;
            _mesh.triangles = tris.ToArray();
            mf.sharedMesh = _mesh;
        }

        /// <summary>Asphalt with a dashed centre line and solid edge lines, tiled along V.</summary>
        private static Texture2D BuildRoadTexture()
        {
            const int w = 64, h = 128;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapModeU = TextureWrapMode.Clamp,
                wrapModeV = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                name = "RoadTex",
            };
            var rng = new System.Random(12345);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)(w - 1);
                float noise = (float)(rng.NextDouble() - 0.5) * 0.06f;
                Color c = new Color(0.115f + noise, 0.115f + noise, 0.125f + noise);

                // solid edge lines
                if (u < 0.06f || u > 0.94f) c = new Color(0.72f, 0.72f, 0.68f);
                // dashed centre line: painted for the first 55% of each tile
                else if (Mathf.Abs(u - 0.5f) < 0.045f && y < h * 0.55f)
                    c = new Color(0.78f, 0.66f, 0.20f);

                px[y * w + x] = c;
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
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
