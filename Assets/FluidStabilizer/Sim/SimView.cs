using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Visualises the simulation: places the truck (world pose), the tank (local
    /// gimbal rotation), and a liquid proxy (a bulk block plus a surface quad
    /// tilted by the first-mode slosh pendulum angles). Read-only — it never
    /// feeds anything back into the sim.
    /// </summary>
    public sealed class SimView : MonoBehaviour
    {
        public SimulationRunner Runner;

        [Header("Rig (wired by SceneBuilder)")]
        public Transform TruckBody;
        public Transform TankPivot;     // child of TruckBody, at the gimbal pivot
        public Transform TankBody;      // child of TankPivot, carries the gimbal rotation
        public Transform LiquidBulk;    // child of TankBody
        public Transform LiquidSurface; // child of TankBody

        private bool _rigged;

        private void LateUpdate()
        {
            if (Runner?.Loop == null) return;
            var loop = Runner.Loop;
            var cfg = loop.Config;

            if (!_rigged) ApplyStaticGeometry(cfg);

            // Truck: world pose.
            var v = loop.VehicleState;
            TruckBody.SetPositionAndRotation(
                Frames.ToUnity(v.OriginWorld) + Vector3.up * 0f,
                Frames.ToUnity(v.OrientationWorldFromBody));

            // Tank: local gimbal rotation about the pivot. R_VT = Ry(pitch)·Rx(roll)
            // in SI; expressed as a Unity local rotation of the tank body.
            var g = loop.GimbalState;
            TankBody.localRotation = Frames.ToUnity(Double3x3.Gimbal(g.Pitch.Angle, g.Roll.Angle));

            // Liquid: bulk height from remaining fill, surface tilted by the
            // pendulum angles (privileged read — visualisation only).
            var p = loop.BuildPrivilegedObservation();
            double h = cfg.Slosh.FillRatio * cfg.Slosh.TankHeightZ; // rough; drains slowly
            float hy = (float)h;
            LiquidBulk.localScale = new Vector3(
                (float)cfg.Slosh.TankWidthY * 0.98f, hy, (float)cfg.Slosh.TankLengthX * 0.98f);
            LiquidBulk.localPosition = new Vector3(0f, hy * 0.5f, 0f);

            LiquidSurface.localScale = new Vector3(
                (float)cfg.Slosh.TankWidthY * 0.98f, 1f, (float)cfg.Slosh.TankLengthX * 0.98f);
            LiquidSurface.localPosition = new Vector3(0f, hy, 0f);
            // slosh pitch is about SI Y (Unity X); slosh roll is about SI X (Unity Z)
            LiquidSurface.localRotation = Quaternion.Euler(
                Mathf.Rad2Deg * (float)p.SloshPitchAngle, 0f, -Mathf.Rad2Deg * (float)p.SloshRollAngle);
        }

        private void ApplyStaticGeometry(SimulationConfig cfg)
        {
            // Pivot placement in the truck body (SI vehicle frame → Unity local).
            TankPivot.localPosition = Frames.ToUnity(cfg.Gimbal.PivotInVehicle);

            // Tank shell size (SI X=length→Unity Z, Y=width→Unity X, Z=height→Unity Y).
            var shell = TankBody.Find("Shell");
            if (shell != null)
                shell.localScale = new Vector3(
                    (float)cfg.Slosh.TankWidthY, (float)cfg.Slosh.TankHeightZ, (float)cfg.Slosh.TankLengthX);

            _rigged = true;
        }
    }
}
