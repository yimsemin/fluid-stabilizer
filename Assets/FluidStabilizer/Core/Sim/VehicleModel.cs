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

        /// <summary>Signed distance from the road centreline (m, +ve = toward road-left). 0 when the road is off.</summary>
        public double LateralOffsetFromRoadCentre;
        /// <summary>Truck heading minus the road heading (rad). 0 when the road is off.</summary>
        public double HeadingErrorFromRoad;
        /// <summary>Absolute road heading at the truck (rad, integrated curvature). 0 when the road is off.</summary>
        public double RoadHeadingRad;

        /// <summary>Tyre slip [0,1]: 0 = full grip, 1 = sliding. &gt;0 only on a hard maneuver near the friction limit (worse on a low-grip road).</summary>
        public double TyreSlip;
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
        private double _lateralOffset;   // m from the road centreline (road on only)
        private double _lastLongAccel;
        private double _lastLatAccel;
        private double _lastYawRate;

        private IRandomSource _slipRng;    // null → no yaw wander (deterministic without a loop seed)
        private double _slip;              // [0,1] lumped tyre-slip state
        private double _slipYaw;           // OU noise for the fishtail
        private double _yawRateGripped;    // tyre yaw rate low-passed while sliding (under-response, then grab)

        public VehicleModel(VehicleConfig cfg)
        {
            _cfg = cfg;
            _pitch = new SecondOrderResponse(cfg.PitchNaturalFreqHz, cfg.PitchDampingRatio);
            _roll = new SecondOrderResponse(cfg.RollNaturalFreqHz, cfg.RollDampingRatio);
            _heave = new SecondOrderResponse(cfg.HeaveNaturalFreqHz, cfg.HeaveDampingRatio);
            Reset();
        }

        public void Reset() => Reset(null);

        /// <param name="rng">A per-episode sub-stream for the tyre-slip yaw wander. Null = no wander.</param>
        public void Reset(IRandomSource rng)
        {
            _speed = _cfg.InitialSpeed;
            _yaw = 0.0;
            _posWorld = Double3.Zero;
            _lateralOffset = 0.0;
            _pitch.Reset();
            _roll.Reset();
            _heave.Reset();
            _lastLongAccel = 0.0;
            _lastLatAccel = 0.0;
            _lastYawRate = 0.0;
            _slipRng = rng?.Fork("slip");
            _slip = 0.0;
            _slipYaw = 0.0;
            _yawRateGripped = 0.0;
        }

        public VehicleState Step(DriverControls controls, RoadInput road, double dt)
            => Step(controls, road, roadHeadingRad: 0.0, roadHalfWidth: 0.0, roadActive: false, dt);

        public VehicleState Step(DriverControls controls, RoadInput road,
            double roadHeadingRad, double roadHalfWidth, bool roadActive, double dt)
        {
            // --- longitudinal ---
            double aLongDesired = controls.Throttle * _cfg.MaxAccel - controls.Brake * _cfg.MaxBrake;
            // gravity component along a graded road opposes climbing
            aLongDesired -= MathD.StandardGravity * Math.Sin(road.Grade);

            // --- lateral from steering (bicycle) ---
            // The driver has full steering authority. With the road on, the
            // curvature is followed automatically (so the truck doesn't leave the
            // road on a bend), there is only a *weak* pull back toward centre —
            // enough that the driver can change lanes and hold a chosen one — and
            // a firm wall at the road edge.
            double steerAngle = controls.Steer * (25.0 * MathD.Deg2Rad); // ±25° road-wheel angle at full lock
            double driverYawRate = _speed * Math.Tan(steerAngle) / Math.Max(0.1, _cfg.Wheelbase);

            // Tyre-force yaw (driver + follow the bend + weak centring) — this is
            // what the friction circle limits.
            double tyreYawRate = driverYawRate;
            double headingErr = 0.0;
            double edgeYawRate = 0.0;
            if (roadActive)
            {
                headingErr = MathD.WrapPi(_yaw - roadHeadingRad);
                tyreYawRate += _speed * road.Curvature
                               - _cfg.LaneReturnGain * _lateralOffset / Math.Max(4.0, _speed);
                double over = Math.Abs(_lateralOffset) - roadHalfWidth;
                if (over > 0.0)
                    edgeYawRate = -Math.Sign(_lateralOffset) * _cfg.RoadEdgeGain * over; // a guardrail, not a tyre force
            }

            double aLat = _speed * tyreYawRate;

            // --- lumped tyre-slip proxy ---
            // Break loose as the demanded acceleration nears the friction limit;
            // recover slowly. While sliding, grip drops further (kinetic < static)
            // and the rear steps out (a low-freq yaw wander) — so the tank slops.
            double aDemand = Math.Sqrt(aLongDesired * aLongDesired + aLat * aLat);
            {
                double ratio = _cfg.FrictionLimit > 1e-6 ? aDemand / _cfg.FrictionLimit : 0.0;
                double slipTarget = MathD.Clamp(
                    (ratio - _cfg.SlipOnsetFraction) / Math.Max(0.05, _cfg.SlipSaturationSpan), 0.0, 1.0);
                double tau = _slip < slipTarget ? _cfg.SlipOnsetTau : _cfg.SlipRecoverTau;
                double a = dt > 0.0 ? 1.0 - Math.Exp(-dt / Math.Max(1e-3, tau)) : 1.0;
                _slip += (slipTarget - _slip) * a;

                // Advance the OU wander every step (drawn from a dedicated stream,
                // so consuming it never shifts the road/sensor/slosh RNG).
                if (_slipRng != null)
                {
                    double b = dt > 0.0 ? 1.0 - Math.Exp(-dt / Math.Max(1e-3, _cfg.SlipYawTau)) : 1.0;
                    _slipYaw += -b * _slipYaw + Math.Sqrt(2.0 * b) * _slipRng.NextGaussian();
                }
            }

            double effLimit = _cfg.FrictionLimit * (1.0 - _cfg.SlipGripLoss * _slip);

            // --- friction circle (tyre forces only) ---
            double aMag = aDemand;
            if (aMag > effLimit && aMag > 1e-9)
            {
                double scale = effLimit / aMag;
                aLongDesired *= scale;
                aLat *= scale;
                tyreYawRate *= scale;
            }

            // While sliding, the tyres under-respond to steering, then "grab" when
            // grip returns (the slip time constant collapses with _slip).
            double slipLagTau = _cfg.SlipYawLag * _slip;
            double lg = dt > 0.0 && slipLagTau > 1e-4 ? 1.0 - Math.Exp(-dt / slipLagTau) : 1.0;
            _yawRateGripped += (tyreYawRate - _yawRateGripped) * lg;
            tyreYawRate = _yawRateGripped;
            aLat = _speed * tyreYawRate;

            double yawRate = tyreYawRate + edgeYawRate
                             + _slip * _cfg.SlipYawWander * _slipYaw;   // fishtail while sliding

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

            // --- lateral position within the road ---
            if (roadActive)
            {
                headingErr = MathD.WrapPi(_yaw - roadHeadingRad);
                _lateralOffset += _speed * Math.Sin(headingErr) * dt;
                // hard backstop: the truck physically cannot leave the road
                double lim = roadHalfWidth + 1.2;
                _lateralOffset = MathD.Clamp(_lateralOffset, -lim, lim);
            }
            else
            {
                _lateralOffset = 0.0;
            }

            // --- suspension: body pitch/roll from load transfer + a one-wheel bump ---
            double roadRoll = roadActive
                ? Math.Atan2(road.SurfaceHeightLeft - road.SurfaceHeightRight, Math.Max(0.3, _cfg.TrackWidthM))
                : 0.0;
            double pitchTarget = _cfg.PitchPerLongAccel * aLong + road.Grade;
            double rollTarget = -_cfg.RollPerLatAccel * aLat + road.Bank + _cfg.RoadRollGain * roadRoll;
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
                LateralAcceleration = aLat,
                LateralOffsetFromRoadCentre = _lateralOffset,
                HeadingErrorFromRoad = headingErr,
                RoadHeadingRad = roadActive ? roadHeadingRad : 0.0,
                TyreSlip = _slip
            };
        }
    }
}
