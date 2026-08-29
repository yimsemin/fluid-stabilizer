using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>
    /// Rigid-body state of the vehicle body (sprung mass) at one instant, all in
    /// SI. Linear/angular quantities of the body-frame origin (rear-axle ground
    /// point). This is ground truth — sensors derive noisy readings from it.
    /// </summary>
    public struct VehicleState
    {
        public Double3 OriginWorld;
        public Double3x3 OrientationWorldFromBody;   // R_WV: body vector -> world
        public Double3 LinearVelocityWorld;
        public Double3 LinearAccelerationBody;       // accel of the origin, expressed in body frame
        public Double3 AngularVelocityBody;          // (roll, pitch, yaw) rates, body frame
        public Double3 AngularAccelerationBody;
        public double Speed;                         // forward speed, m/s
        public double LongitudinalAcceleration;      // m/s²
        public double LateralAcceleration;           // m/s²
    }

    /// <summary>
    /// Reduced vehicle model (design-review §8): a point mass with a
    /// friction-circle "bicycle" longitudinal/lateral response, plus a 3-DOF
    /// sprung mass (body pitch/roll from load transfer, heave from the road
    /// surface). No <c>WheelCollider</c>, no PhysX. Road grade/bank/roughness
    /// come in through <see cref="RoadInput"/> (all-zero = flat straight road).
    /// </summary>
    public sealed class VehicleModel
    {
        private readonly VehicleConfig _cfg;
        private readonly SecondOrderResponse _pitch;
        private readonly SecondOrderResponse _roll;
        private readonly SecondOrderResponse _heave;

        private double _speed;
        private double _yaw;
        private Double3 _posWorld;
        private double _lastLongAccel;
        private double _lastLatAccel;
        private double _lastYawRate;

        public VehicleModel(VehicleConfig cfg)
        {
            _cfg = cfg;
            _pitch = new SecondOrderResponse(cfg.PitchNaturalFreqHz, cfg.PitchDampingRatio);
            _roll = new SecondOrderResponse(cfg.RollNaturalFreqHz, cfg.RollDampingRatio);
            _heave = new SecondOrderResponse(cfg.HeaveNaturalFreqHz, cfg.HeaveDampingRatio);
            Reset();
        }

        public void Reset()
        {
            _speed = _cfg.InitialSpeed;
            _yaw = 0.0;
            _posWorld = Double3.Zero;
            _pitch.Reset();
            _roll.Reset();
            _heave.Reset();
            _lastLongAccel = 0.0;
            _lastLatAccel = 0.0;
            _lastYawRate = 0.0;
        }

        public VehicleState Step(DriverControls controls, RoadInput road, double dt)
        {
            // --- longitudinal ---
            double aLongDesired = controls.Throttle * _cfg.MaxAccel - controls.Brake * _cfg.MaxBrake;
            // gravity component along a graded road opposes climbing
            aLongDesired -= MathD.StandardGravity * Math.Sin(road.Grade);

            // --- lateral from steering (bicycle) ---
            double steerAngle = controls.Steer * (25.0 * MathD.Deg2Rad); // ±25° road-wheel angle at full lock
            double yawRate = _speed * Math.Tan(steerAngle) / Math.Max(0.1, _cfg.Wheelbase);
            double aLat = _speed * yawRate;

            // --- friction circle ---
            double aMag = Math.Sqrt(aLongDesired * aLongDesired + aLat * aLat);
            if (aMag > _cfg.FrictionLimit && aMag > 1e-9)
            {
                double scale = _cfg.FrictionLimit / aMag;
                aLongDesired *= scale;
                aLat *= scale;
                yawRate *= scale;
            }

            // --- integrate speed (no reverse) ---
            double newSpeed = _speed + aLongDesired * dt;
            double aLong;
            if (newSpeed < 0.0 && dt > 0.0)
            {
                aLong = -_speed / dt; // decelerate exactly to rest this step
                newSpeed = 0.0;
            }
            else
            {
                aLong = aLongDesired;
            }
            _speed = newSpeed;

            // --- integrate heading & world position ---
            _yaw = MathD.WrapPi(_yaw + yawRate * dt);
            var headingDir = new Double3(Math.Cos(_yaw), Math.Sin(_yaw), 0.0);
            Double3 velWorld = headingDir * _speed;
            _posWorld += velWorld * dt;

            // --- suspension: body pitch/roll from load transfer ---
            double pitchTarget = _cfg.PitchPerLongAccel * aLong + road.Grade;
            double rollTarget = -_cfg.RollPerLatAccel * aLat + road.Bank;
            double pitchAccel = _pitch.AccelerationToward(pitchTarget);
            double rollAccel = _roll.AccelerationToward(rollTarget);
            _pitch.Step(pitchTarget, dt);
            _roll.Step(rollTarget, dt);

            // --- suspension heave: the body follows the road surface through a
            //     spring-mass-damper (the ~1.5 Hz ride mode). heaveAccel is the
            //     absolute vertical acceleration the tank base feels. ---
            double heaveAccel = _heave.AccelerationToward(road.SurfaceHeight);
            _heave.Step(road.SurfaceHeight, dt);

            double yawAccel = dt > 0.0 ? (yawRate - _lastYawRate) / dt : 0.0;
            _lastYawRate = yawRate;
            _lastLongAccel = aLong;
            _lastLatAccel = aLat;

            // --- assemble state ---
            var rWV = Double3x3.FromYawPitchRoll(_yaw, _pitch.Value, _roll.Value);

            // Acceleration of the origin in body frame: forward = aLong, left = aLat,
            // up = suspension heave. (Small-angle: heading-aligned axes ≈ body axes.)
            var aBody = new Double3(aLong, aLat, heaveAccel);

            var omegaBody = new Double3(_roll.Velocity, _pitch.Velocity, yawRate);
            var alphaBody = new Double3(rollAccel, pitchAccel, yawAccel);

            return new VehicleState
            {
                OriginWorld = _posWorld + new Double3(0.0, 0.0, _heave.Value),
                OrientationWorldFromBody = rWV,
                LinearVelocityWorld = velWorld,
                LinearAccelerationBody = aBody,
                AngularVelocityBody = omegaBody,
                AngularAccelerationBody = alphaBody,
                Speed = _speed,
                LongitudinalAcceleration = aLong,
                LateralAcceleration = aLat
            };
        }
    }
}
