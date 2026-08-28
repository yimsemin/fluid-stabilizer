using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.SloshModels;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Visualises the simulation: places the truck (world pose), the tank (local
    /// gimbal rotation), and the liquid. Two liquid representations, picked to
    /// match the active slosh model:
    ///   * pendulum ROM  → a translucent bulk block + a surface quad tilted by
    ///     the two first-mode pendulum angles (a proxy — the ROM has no surface);
    ///   * shallow water → a deforming mesh driven by the solver's height field
    ///     (real travelling waves that climb the walls and slop over the rim).
    /// Read-only — it never feeds anything back into the sim.
    /// </summary>
    public sealed class SimView : MonoBehaviour
    {
        public SimulationRunner Runner;

        [Header("Rig (wired by SceneBuilder)")]
        public Transform TruckBody;
        public Transform TankPivot;     // child of TruckBody, at the gimbal pivot
        public Transform TankBody;      // child of TankPivot, carries the gimbal rotation
        public Transform LiquidBulk;    // child of TankBody — pendulum-ROM proxy
        public Transform LiquidSurface; // child of TankBody — pendulum-ROM proxy
        public MeshFilter LiquidMesh;   // child of TankBody — shallow-water height field

        private bool _rigged;
        private int _meshNx, _meshNy;
        private Vector3[] _meshVerts;
        private Mesh _mesh;

        private void LateUpdate()
        {
            if (Runner?.Loop == null) return;
            var loop = Runner.Loop;
            var cfg = loop.Config;

            if (!_rigged) ApplyStaticGeometry(cfg);

            // Truck: world pose.
            var v = loop.VehicleState;
            TruckBody.SetPositionAndRotation(
                Frames.ToUnity(v.OriginWorld),
                Frames.ToUnity(v.OrientationWorldFromBody));

            // Tank: local gimbal rotation about the pivot. R_VT = Ry(pitch)·Rx(roll).
            var g = loop.GimbalState;
            TankBody.localRotation = Frames.ToUnity(Double3x3.Gimbal(g.Pitch.Angle, g.Roll.Angle));

            bool shallow = loop.SloshModel is ShallowWaterSloshModel;
            if (LiquidBulk != null) LiquidBulk.gameObject.SetActive(!shallow);
            if (LiquidSurface != null) LiquidSurface.gameObject.SetActive(!shallow);
            if (LiquidMesh != null) LiquidMesh.gameObject.SetActive(shallow);

            if (shallow) UpdateHeightFieldMesh((ShallowWaterSloshModel)loop.SloshModel);
            else UpdatePendulumProxy(loop, cfg);
        }

        // ---- pendulum ROM proxy: bulk block + tilted surface quad ----

        private void UpdatePendulumProxy(SimulationLoop loop, SimulationConfig cfg)
        {
            var p = loop.BuildPrivilegedObservation();
            float hy = (float)(cfg.Slosh.FillRatio * cfg.Slosh.TankHeightZ);

            LiquidBulk.localScale = new Vector3(
                (float)cfg.Slosh.TankWidthY * 0.98f, hy, (float)cfg.Slosh.TankLengthX * 0.98f);
            LiquidBulk.localPosition = new Vector3(0f, hy * 0.5f, 0f);

            LiquidSurface.localScale = new Vector3(
                (float)cfg.Slosh.TankWidthY * 0.98f, 1f, (float)cfg.Slosh.TankLengthX * 0.98f);
            LiquidSurface.localPosition = new Vector3(0f, hy, 0f);
            // slosh pitch is about SI Y (Unity X); slosh roll is about SI X (Unity Z).
            // Clamp the *visual* tilt — the ROM pendulum can swing past where a
            // real first-mode surface stays flat.
            float visPitch = Mathf.Clamp(Mathf.Rad2Deg * (float)p.SloshPitchAngle, -35f, 35f);
            float visRoll = Mathf.Clamp(Mathf.Rad2Deg * (float)p.SloshRollAngle, -35f, 35f);
            LiquidSurface.localRotation = Quaternion.Euler(visPitch, 0f, -visRoll);
        }

        // ---- shallow-water: deforming height-field mesh ----

        private void UpdateHeightFieldMesh(ShallowWaterSloshModel m)
        {
            var (nx, ny) = m.GridSize;
            if (_mesh == null || _meshNx != nx || _meshNy != ny) BuildMesh(m, nx, ny);

            // TankBody local frame: y = 0 at the tank floor (same convention the
            // proxy uses). Grid point (xc, yc, h) in the floor-origin tank frame
            // maps to Unity local via Frames.ToUnity = (−yc, h, xc).
            for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                Double3 c = m.CellCenter(i, j);
                _meshVerts[i * ny + j] = new Vector3((float)(-c.Y), (float)m.SurfaceHeight(i, j), (float)c.X);
            }
            _mesh.vertices = _meshVerts;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }

        private void BuildMesh(ShallowWaterSloshModel m, int nx, int ny)
        {
            _meshNx = nx;
            _meshNy = ny;
            _meshVerts = new Vector3[nx * ny];

            var uv = new Vector2[nx * ny];
            for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
                uv[i * ny + j] = new Vector2(i / (float)(nx - 1), j / (float)(ny - 1));

            var tris = new int[(nx - 1) * (ny - 1) * 6];
            int t = 0;
            for (int i = 0; i < nx - 1; i++)
            for (int j = 0; j < ny - 1; j++)
            {
                int a = i * ny + j, b = i * ny + (j + 1), cc = (i + 1) * ny + j, d = (i + 1) * ny + (j + 1);
                tris[t++] = a; tris[t++] = b; tris[t++] = cc;
                tris[t++] = cc; tris[t++] = b; tris[t++] = d;
            }

            for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                Double3 c = m.CellCenter(i, j);
                _meshVerts[i * ny + j] = new Vector3((float)(-c.Y), 0f, (float)c.X);
            }

            _mesh = new Mesh { name = "ShallowWaterSurface" };
            _mesh.MarkDynamic();
            _mesh.vertices = _meshVerts;
            _mesh.uv = uv;
            _mesh.triangles = tris;
            _mesh.RecalculateNormals();
            LiquidMesh.sharedMesh = _mesh;
        }

        private void ApplyStaticGeometry(SimulationConfig cfg)
        {
            TankPivot.localPosition = Frames.ToUnity(cfg.Gimbal.PivotInVehicle);

            var shell = TankBody.Find("Shell");
            if (shell != null)
                shell.localScale = new Vector3(
                    (float)cfg.Slosh.TankWidthY, (float)cfg.Slosh.TankHeightZ, (float)cfg.Slosh.TankLengthX);

            _rigged = true;
        }
    }
}
