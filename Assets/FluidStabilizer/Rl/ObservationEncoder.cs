using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core;

namespace FluidStabilizer.Rl
{
    /// <summary>
    /// Turns a <see cref="SensorBundle"/> (the only thing the actor is allowed to
    /// see — noisy, delayed, sensor-derived) plus the last action into a flat,
    /// roughly unit-scaled <c>float[]</c>, and keeps a frame stack of the last N
    /// control steps (design-review §11–§12: memory is required to infer the
    /// hidden slosh state).
    ///
    /// Layout of one frame (see <see cref="SingleFrameSize"/> = 39):
    ///   [0..2]   vehicle IMU specific force / 10
    ///   [3..5]   vehicle IMU angular velocity / 3
    ///   [6..8]   tank IMU specific force / 10
    ///   [9..11]  tank IMU angular velocity / 3
    ///   [12..15] joint pitch/roll angle (/0.5), pitch/roll rate (/3.5)
    ///   [16..19] motor pitch/roll current (/20), pitch/roll torque (/900)
    ///   [20..23] 4 load cells / 4000
    ///   [24..27] 3 level probes / 1.0, then HasLevels flag (0/1)
    ///   [28..32] CAN speed/30, steer/0.5, throttle, brake, HasVehicleCan flag
    ///   [33..34] last action (pitch, roll), already in [-1, 1]
    ///   [35..36] low-passed felt-tilt in the tank frame (pitch, roll) / 0.5
    ///   [37..38] rate of that felt-tilt / 3
    /// Stacked oldest → newest; <see cref="StackedSize"/> = SingleFrameSize · N.
    /// </summary>
    public sealed class ObservationEncoder
    {
        public const int SingleFrameSize = 39;

        private readonly int _stack;
        private readonly float[] _ring;      // _stack × SingleFrameSize, circular by frame
        private long _count;                 // frames written this episode

        private readonly LowPass _tiltPitch = new LowPass(2.0);
        private readonly LowPass _tiltRoll = new LowPass(2.0);
        private double _prevTiltPitch, _prevTiltRoll, _prevStamp;
        private bool _havePrev;

        public ObservationEncoder(int frameStackLength = 6)
        {
            if (frameStackLength < 1) throw new ArgumentOutOfRangeException(nameof(frameStackLength));
            _stack = frameStackLength;
            _ring = new float[_stack * SingleFrameSize];
        }

        public int FrameStackLength => _stack;
        public int StackedSize => _stack * SingleFrameSize;

        public void Reset()
        {
            Array.Clear(_ring, 0, _ring.Length);
            _count = 0;
            _tiltPitch.Reset(0.0);
            _tiltRoll.Reset(0.0);
            _prevTiltPitch = _prevTiltRoll = _prevStamp = 0.0;
            _havePrev = false;
        }

        /// <summary>
        /// Encode one control step and return the current stacked observation
        /// (a fresh array, oldest frame first). Before the stack has filled, the
        /// missing older frames read as zeros.
        /// </summary>
        public float[] Observe(in SensorBundle b, double lastActionPitch, double lastActionRoll)
        {
            double dt = _havePrev ? b.StampSeconds - _prevStamp : 0.0;
            if (dt <= 0.0) dt = 1.0e-2;

            double tiltP = TiltGeometry.DesiredPitch(b.TankImu.SpecificForce);
            double tiltR = TiltGeometry.DesiredRoll(b.TankImu.SpecificForce);
            double lpP = _tiltPitch.Step(tiltP, dt);
            double lpR = _tiltRoll.Step(tiltR, dt);
            double tiltRateP = _havePrev ? (lpP - _prevTiltPitch) / dt : 0.0;
            double tiltRateR = _havePrev ? (lpR - _prevTiltRoll) / dt : 0.0;
            _prevTiltPitch = lpP;
            _prevTiltRoll = lpR;
            _prevStamp = b.StampSeconds;
            _havePrev = true;

            int o = (int)(_count % _stack) * SingleFrameSize;
            var f = _ring;

            f[o + 0] = (float)(b.VehicleImu.SpecificForce.X / 10.0);
            f[o + 1] = (float)(b.VehicleImu.SpecificForce.Y / 10.0);
            f[o + 2] = (float)(b.VehicleImu.SpecificForce.Z / 10.0);
            f[o + 3] = (float)(b.VehicleImu.AngularVelocity.X / 3.0);
            f[o + 4] = (float)(b.VehicleImu.AngularVelocity.Y / 3.0);
            f[o + 5] = (float)(b.VehicleImu.AngularVelocity.Z / 3.0);

            f[o + 6] = (float)(b.TankImu.SpecificForce.X / 10.0);
            f[o + 7] = (float)(b.TankImu.SpecificForce.Y / 10.0);
            f[o + 8] = (float)(b.TankImu.SpecificForce.Z / 10.0);
            f[o + 9] = (float)(b.TankImu.AngularVelocity.X / 3.0);
            f[o + 10] = (float)(b.TankImu.AngularVelocity.Y / 3.0);
            f[o + 11] = (float)(b.TankImu.AngularVelocity.Z / 3.0);

            f[o + 12] = (float)(b.Joints.PitchAngle / 0.5);
            f[o + 13] = (float)(b.Joints.RollAngle / 0.5);
            f[o + 14] = (float)(b.Joints.PitchRate / 3.5);
            f[o + 15] = (float)(b.Joints.RollRate / 3.5);

            f[o + 16] = (float)(b.Motors.PitchCurrent / 20.0);
            f[o + 17] = (float)(b.Motors.RollCurrent / 20.0);
            f[o + 18] = (float)(b.Motors.PitchTorque / 900.0);
            f[o + 19] = (float)(b.Motors.RollTorque / 900.0);

            double[] cells = b.LoadCells.Forces;
            for (int i = 0; i < 4; i++)
                f[o + 20 + i] = cells != null && i < cells.Length ? (float)(cells[i] / 4000.0) : 0f;

            double[] lv = b.HasLevels ? b.Levels.Heights : null;
            for (int i = 0; i < 3; i++)
                f[o + 24 + i] = lv != null && i < lv.Length ? (float)lv[i] : 0f;
            f[o + 27] = b.HasLevels ? 1f : 0f;

            if (b.HasVehicleCan)
            {
                f[o + 28] = (float)(b.VehicleCan.SpeedMetersPerSecond / 30.0);
                f[o + 29] = (float)(b.VehicleCan.SteerAngleRadians / 0.5);
                f[o + 30] = (float)b.VehicleCan.Throttle;
                f[o + 31] = (float)b.VehicleCan.Brake;
                f[o + 32] = 1f;
            }
            else
            {
                f[o + 28] = f[o + 29] = f[o + 30] = f[o + 31] = f[o + 32] = 0f;
            }

            f[o + 33] = (float)lastActionPitch;
            f[o + 34] = (float)lastActionRoll;
            f[o + 35] = (float)(lpP / 0.5);
            f[o + 36] = (float)(lpR / 0.5);
            f[o + 37] = (float)(tiltRateP / 3.0);
            f[o + 38] = (float)(tiltRateR / 3.0);

            _count++;
            return Stacked();
        }

        /// <summary>
        /// The current stacked observation, oldest frame first. Before the stack
        /// has filled, the oldest positions read as zeros.
        /// </summary>
        public float[] Stacked()
        {
            var outp = new float[StackedSize];
            for (int k = 0; k < _stack; k++)
            {
                // logical position k (0 = oldest) → frame (_count - _stack + k)
                long frame = _count - _stack + k;
                if (frame < 0) continue; // not written yet → leave zero
                int src = (int)(frame % _stack) * SingleFrameSize;
                Array.Copy(_ring, src, outp, k * SingleFrameSize, SingleFrameSize);
            }
            return outp;
        }
    }
}
