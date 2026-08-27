using System;
using FluidStabilizer.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Human driving input, polled from the keyboard and any connected gamepad
    /// through the Input System. Throttle/brake/steer are smoothed toward the raw
    /// input so a laden-truck feel comes through even with binary keys.
    ///
    /// Lives in <c>FluidStabilizer.Sim</c> because it is the only driver that
    /// touches <c>UnityEngine</c>. The simulation core only sees
    /// <see cref="DriverControls"/>.
    /// </summary>
    public sealed class HumanDriverInput : IDriverInput
    {
        private readonly float _steerRateUp = 2.2f;   // 1/s toward target
        private readonly float _steerRateCenter = 3.5f;
        private readonly float _pedalRate = 2.5f;

        private double _throttle, _brake, _steer;
        private double _lastT;

        public string Name => "Human";

        public void Reset()
        {
            _throttle = _brake = _steer = 0.0;
            _lastT = 0.0;
        }

        public DriverControls Sample(double simTimeSeconds)
        {
            double dt = simTimeSeconds - _lastT;
            if (dt <= 0) dt = 1.0 / 600.0;
            _lastT = simTimeSeconds;

            ReadRaw(out double thrTarget, out double brkTarget, out double steerTarget);

            _throttle = MoveToward(_throttle, thrTarget, _pedalRate * dt);
            _brake = MoveToward(_brake, brkTarget, _pedalRate * dt);

            double steerRate = Math.Abs(steerTarget) > Math.Abs(_steer) ? _steerRateUp : _steerRateCenter;
            _steer = MoveToward(_steer, steerTarget, steerRate * dt);

            return new DriverControls
            {
                Throttle = Clamp01(_throttle),
                Brake = Clamp01(_brake),
                Steer = Math.Max(-1.0, Math.Min(1.0, _steer))
            };
        }

        private static void ReadRaw(out double throttle, out double brake, out double steer)
        {
            throttle = 0; brake = 0; steer = 0;

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) throttle = 1;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) brake = 1;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steer += 1;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steer -= 1;
            }

            var gp = Gamepad.current;
            if (gp != null)
            {
                throttle = Math.Max(throttle, gp.rightTrigger.ReadValue());
                brake = Math.Max(brake, gp.leftTrigger.ReadValue());
                float gx = gp.leftStick.ReadValue().x;
                if (Mathf.Abs(gx) > 0.12f) steer = -gx; // +stick right → steer right (−Y frame)
            }

            steer = Math.Max(-1.0, Math.Min(1.0, steer));
        }

        private static double MoveToward(double c, double t, double maxDelta)
        {
            double d = t - c;
            if (Math.Abs(d) <= maxDelta) return t;
            return c + Math.Sign(d) * maxDelta;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
