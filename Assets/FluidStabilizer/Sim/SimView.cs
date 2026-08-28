using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.SloshModels;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Visualises the simulation: places the truck (world pose), the tank (local
    /// gimbal rotation), and the liquid as a closed, deforming water body whose
    /// top surface is the slosh model's free surface.
    ///
    /// One renderer for both slosh models: a grid mesh (top surface + side skirts
    /// down to the floor + a floor face) whose top vertices follow a height
    /// field. Shallow water feeds its real grid; the pendulum ROM feeds a tilted
    /// plane from its two first-mode angles (a proxy — the ROM has no surface).
    /// It never overhangs the tank walls: the mesh footprint is exactly the tank
    /// interior and heights are clamped to the rim + a small margin, so anything
    /// "over the top" is genuine rim overflow.
    ///
    /// Read-only — it never feeds anything back into the sim.
    /// </summary>
    public sealed class SimView : MonoBehaviour
    {
        public SimulationRunner Runner;

        [Header("Rig (wired by SceneBuilder)")]
        public Transform TruckBody;
        public Transform TankPivot;   // child of TruckBody, at the gimbal pivot
        public Transform TankBody;    // child of TankPivot, carries the gimbal rotation
        public MeshFilter LiquidMesh; // child of TankBody — the water body

        [Header("Presentation")]
        [Tooltip("Cosmetic upward offset of the tank so the surface tilt and any " +
                 "overflow are clearly visible above the truck body. Physics is unaffected.")]
        public float TankVisualLift = 0.35f;

        // pendulum-ROM proxy surface resolution (a tilted plane needs little)
        private const int ProxyNx = 24;
        private const int ProxyNy = 14;

        private bool _rigged;
        private int _nx, _ny;
        private double _lx, _ly, _tankH, _restLevel;
        private Vector3[] _verts;   // [0, n) top, [n, 2n) floor
        private Mesh _mesh;

        private Renderer _frame;
        private Color _frameBase;
        private double _lastOverflow;
        private float _spillFlash;

        private void LateUpdate() => RenderTick();

        /// <summary>One visual update. Public so a test can drive it (no Unity player loop).</summary>
        public void RenderTick()
        {
            if (Runner?.Loop == null || LiquidMesh == null) return;
            var loop = Runner.Loop;
            var cfg = loop.Config;

            if (!_rigged) ApplyStaticGeometry(cfg);

            var v = loop.VehicleState;
            TruckBody.SetPositionAndRotation(
                Frames.ToUnity(v.OriginWorld),
                Frames.ToUnity(v.OrientationWorldFromBody));

            var g = loop.GimbalState;
            TankBody.localRotation = Frames.ToUnity(Double3x3.Gimbal(g.Pitch.Angle, g.Roll.Angle));

            _lx = cfg.Slosh.TankLengthX;
            _ly = cfg.Slosh.TankWidthY;
            _tankH = cfg.Slosh.TankHeightZ;
            _restLevel = cfg.Slosh.FillRatio * cfg.Slosh.TankHeightZ;

            if (loop.SloshModel is ShallowWaterSloshModel sw) UpdateFromShallowWater(sw);
            else UpdateFromPendulum(loop);

            UpdateSpillFlash(loop);
        }

        private void UpdateSpillFlash(SimulationLoop loop)
        {
            if (_frame == null) return;
            double spill = loop.LatestGroundTruth.CumulativeOverflowVolume;
            if (spill > _lastOverflow + 1e-9) _spillFlash = 1f;
            _lastOverflow = spill;
            float dt = Time.deltaTime > 0f ? Time.deltaTime : 1f / 60f;
            _spillFlash = Mathf.Max(0f, _spillFlash - dt * 1.5f);
            _frame.material.color = Color.Lerp(_frameBase, new Color(1f, 0.25f, 0.15f), _spillFlash);
        }

        // ---- height sources ----

        private void UpdateFromShallowWater(ShallowWaterSloshModel m)
        {
            var (nx, ny) = m.GridSize;
            EnsureMesh(nx, ny);
            for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
                SetColumn(i, j, m.CellCenter(i, j).X, m.CellCenter(i, j).Y, m.SurfaceHeight(i, j));
            CommitMesh();
        }

        private void UpdateFromPendulum(SimulationLoop loop)
        {
            EnsureMesh(ProxyNx, ProxyNy);
            var p = loop.BuildPrivilegedObservation();
            // Surface tilt from the two first-mode angles; sign matches the
            // shallow-water convention (θ ≈ d(depth)/d(axis)). Slope clamped so a
            // large ROM excursion cannot fold the plane.
            double sx = Clamp(p.SloshPitchAngle, -0.8, 0.8);
            double sy = Clamp(p.SloshRollAngle, -0.8, 0.8);

            for (int i = 0; i < ProxyNx; i++)
            for (int j = 0; j < ProxyNy; j++)
            {
                double x = (-_lx * 0.5) + (i + 0.5) * (_lx / ProxyNx);
                double y = (-_ly * 0.5) + (j + 0.5) * (_ly / ProxyNy);
                SetColumn(i, j, x, y, _restLevel + sx * x + sy * y);
            }
            CommitMesh();
        }

        // ---- closed water-body mesh ----

        private void SetColumn(int i, int j, double xTank, double yTank, double height)
        {
            // TankBody local: shell is centred on the origin, so the floor is at
            // y = -tankH/2. Tank frame (x fwd, y left, z up) → Unity (−y, z, x).
            float floorY = (float)(-_tankH * 0.5);
            float h = (float)Clamp(height, 0.006, _tankH * 1.12);
            int k = i * _ny + j;
            _verts[k] = new Vector3((float)(-yTank), floorY + h, (float)xTank);
            _verts[k + _nx * _ny] = new Vector3((float)(-yTank), floorY, (float)xTank);
        }

        private void EnsureMesh(int nx, int ny)
        {
            if (_mesh != null && _nx == nx && _ny == ny) return;
            _nx = nx;
            _ny = ny;
            int n = nx * ny;
            _verts = new Vector3[2 * n];

            var uv = new Vector2[2 * n];
            for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                var t = new Vector2(i / (float)(nx - 1), j / (float)(ny - 1));
                uv[i * ny + j] = t;
                uv[i * ny + j + n] = t;
            }

            var tris = new System.Collections.Generic.List<int>((nx - 1) * (ny - 1) * 12);
            // top surface (faces +y)
            for (int i = 0; i < nx - 1; i++)
            for (int j = 0; j < ny - 1; j++)
            {
                int a = i * ny + j, b = i * ny + j + 1, c = (i + 1) * ny + j, d = (i + 1) * ny + j + 1;
                tris.Add(a); tris.Add(b); tris.Add(c);
                tris.Add(c); tris.Add(b); tris.Add(d);
            }
            // floor (faces -y): same grid, offset by n, reversed winding
            for (int i = 0; i < nx - 1; i++)
            for (int j = 0; j < ny - 1; j++)
            {
                int a = n + i * ny + j, b = n + i * ny + j + 1, c = n + (i + 1) * ny + j, d = n + (i + 1) * ny + j + 1;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
            // four skirts (top ring → floor ring)
            for (int j = 0; j < ny - 1; j++) AddQuad(tris, Top(0, j), Top(0, j + 1), n + Idx(0, j), n + Idx(0, j + 1), true);
            for (int j = 0; j < ny - 1; j++) AddQuad(tris, Top(nx - 1, j), Top(nx - 1, j + 1), n + Idx(nx - 1, j), n + Idx(nx - 1, j + 1), false);
            for (int i = 0; i < nx - 1; i++) AddQuad(tris, Top(i, 0), Top(i + 1, 0), n + Idx(i, 0), n + Idx(i + 1, 0), false);
            for (int i = 0; i < nx - 1; i++) AddQuad(tris, Top(i, ny - 1), Top(i + 1, ny - 1), n + Idx(i, ny - 1), n + Idx(i + 1, ny - 1), true);

            _mesh = new Mesh { name = "WaterBody" };
            _mesh.MarkDynamic();
            _mesh.vertices = new Vector3[2 * n];
            _mesh.uv = uv;
            _mesh.triangles = tris.ToArray();
            LiquidMesh.sharedMesh = _mesh;

            int Idx(int i, int j) => i * ny + j;
            int Top(int i, int j) => i * ny + j;
        }

        private static void AddQuad(System.Collections.Generic.List<int> tris, int t0, int t1, int b0, int b1, bool flip)
        {
            if (flip) { tris.Add(t0); tris.Add(b0); tris.Add(t1); tris.Add(t1); tris.Add(b0); tris.Add(b1); }
            else { tris.Add(t0); tris.Add(t1); tris.Add(b0); tris.Add(b1); tris.Add(b0); tris.Add(t1); }
        }

        private void CommitMesh()
        {
            _mesh.vertices = _verts;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }

        private void ApplyStaticGeometry(SimulationConfig cfg)
        {
            TankPivot.localPosition =
                Frames.ToUnity(cfg.Gimbal.PivotInVehicle) + Vector3.up * TankVisualLift;

            var shell = TankBody.Find("Shell");
            if (shell != null)
            {
                shell.localScale = new Vector3(
                    (float)cfg.Slosh.TankWidthY, (float)cfg.Slosh.TankHeightZ, (float)cfg.Slosh.TankLengthX);
                _frame = shell.GetComponent<Renderer>();
                if (_frame != null) _frameBase = _frame.sharedMaterial.color;
            }

            _rigged = true;
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        // exposed for tests / diagnostics
        public int VertexCount => _verts?.Length ?? 0;
        public Vector3[] MeshVertices => _verts;
        public (int nx, int ny) MeshGrid => (_nx, _ny);
    }
}
