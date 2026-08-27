using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim.Sensors
{
    /// <summary>
    /// Turns true state into the realistic, imperfect <see cref="SensorBundle"/>
    /// the controller is allowed to see. v1 models per-sample Gaussian noise,
    /// per-episode constant bias (IMUs), and encoder quantisation. Bias
    /// random-walk, scale-factor error, axis misalignment and dropped frames are
    /// added in the domain-randomization step (design-review §13).
    ///
    /// Bundle transport latency is handled by the simulation loop, not here.
    /// </summary>
    public sealed class SensorSuite
    {
        private readonly SensorConfig _cfg;
        private readonly SloshConfig _sloshCfg;
        private IRandomSource _noise;

        // Per-episode constant biases.
        private Double3 _vehAccelBias, _vehGyroBias, _tankAccelBias, _tankGyroBias;

        public SensorSuite(SensorConfig cfg, SloshConfig sloshCfg)
        {
            _cfg = cfg;
            _sloshCfg = sloshCfg;
        }

        public void Reset(IRandomSource rng)
        {
            _noise = rng.Fork("sensor-noise");
            var biasRng = rng.Fork("sensor-bias");

            _vehAccelBias = RandomVec(biasRng, _cfg.ImuAccelBias);
            _vehGyroBias = RandomVec(biasRng, _cfg.ImuGyroBias);
            _tankAccelBias = RandomVec(biasRng, _cfg.ImuAccelBias);
            _tankGyroBias = RandomVec(biasRng, _cfg.ImuGyroBias);

            if (!_cfg.Enabled)
            {
                _vehAccelBias = _vehGyroBias = _tankAccelBias = _tankGyroBias = Double3.Zero;
            }
        }

        public SensorBundle Sample(double simTime, in VehicleState v, in GimbalState g,
            in TankKinematicState tank, in SloshOutput slosh)
        {
            var bundle = new SensorBundle { StampSeconds = simTime };

            // --- Vehicle IMU (mounted at the body origin in v1) ---
            Double3 gBody = v.OrientationWorldFromBody.InverseTimes(new Double3(0, 0, -MathD.StandardGravity));
            Double3 vehSpecific = v.LinearAccelerationBody - gBody;
            bundle.VehicleImu = new ImuSample
            {
                StampSeconds = simTime,
                SpecificForce = Noisy(vehSpecific + _vehAccelBias, _cfg.ImuAccelNoiseDensity),
                AngularVelocity = Noisy(v.AngularVelocityBody + _vehGyroBias, _cfg.ImuGyroNoiseDensity)
            };

            // --- Tank IMU (mounted at the pivot) ---
            Double3 tankSpecific = tank.LinearAccelerationTankFrame - tank.GravityTankFrame;
            bundle.TankImu = new ImuSample
            {
                StampSeconds = simTime,
                SpecificForce = Noisy(tankSpecific + _tankAccelBias, _cfg.ImuAccelNoiseDensity),
                AngularVelocity = Noisy(tank.AngularVelocityTankFrame + _tankGyroBias, _cfg.ImuGyroNoiseDensity)
            };

            // --- Joint encoders ---
            bundle.Joints = new JointStateSample
            {
                StampSeconds = simTime,
                PitchAngle = Encoder(g.Pitch.Angle),
                RollAngle = Encoder(g.Roll.Angle),
                PitchRate = g.Pitch.Rate + Gauss(_cfg.EncoderNoiseRad * 20.0),
                RollRate = g.Roll.Rate + Gauss(_cfg.EncoderNoiseRad * 20.0)
            };

            // --- Motor state ---
            bundle.Motors = new MotorStateSample
            {
                StampSeconds = simTime,
                PitchCurrent = g.Pitch.MotorCurrent + Gauss(_cfg.MotorCurrentNoiseA),
                RollCurrent = g.Roll.MotorCurrent + Gauss(_cfg.MotorCurrentNoiseA),
                PitchTorque = g.Pitch.AppliedTorque + Gauss(_cfg.MotorCurrentNoiseA * 5.0),
                RollTorque = g.Roll.AppliedTorque + Gauss(_cfg.MotorCurrentNoiseA * 5.0)
            };

            // --- Load cells: 4 corners of the tank cradle, at (±cx, ±cy) in the
            //     tank frame. Each reads the tank-Z force it transmits. The total
            //     normal force and its moment redistribution come from the dry
            //     tank inertia plus the slosh reaction, so the cells carry a
            //     partial, realistic signature of the liquid CoM shift.
            double cx = 0.4 * _sloshCfg.TankLengthX;
            double cy = 0.4 * _sloshCfg.TankWidthY;

            // F_support = m_dry (a - g) - F_slosh   (Newton's 2nd law on the tank)
            double dryZ = _sloshCfg.DryMass * (tank.LinearAccelerationTankFrame.Z - tank.GravityTankFrame.Z);
            double fzTotal = dryZ - slosh.ReactionForceTankFrame.Z;

            // Support moments about the tank X/Y axes (dry CoM at the pivot ⇒ no dry moment).
            double mxs = -slosh.ReactionTorqueTankFrame.X;
            double mys = -slosh.ReactionTorqueTankFrame.Y;

            double baseCell = fzTotal / 4.0;
            double dMx = mxs / (4.0 * cy); // ± per cell by sign of y
            double dMy = mys / (4.0 * cx); // ± per cell by sign of x

            bundle.LoadCells = new LoadCellSample
            {
                StampSeconds = simTime,
                Forces = new[]
                {
                    baseCell + dMx - dMy + Gauss(_cfg.LoadCellNoiseN), // (+x, +y)
                    baseCell - dMx - dMy + Gauss(_cfg.LoadCellNoiseN), // (+x, -y)
                    baseCell + dMx + dMy + Gauss(_cfg.LoadCellNoiseN), // (-x, +y)
                    baseCell - dMx + dMy + Gauss(_cfg.LoadCellNoiseN)  // (-x, -y)
                }
            };

            // --- Optional sensors ---
            if (_cfg.EnableLevelSensors)
            {
                int n = Math.Max(1, _cfg.LevelSensorCount);
                var heights = new double[n];
                double rest = _sloshCfg.FillRatio * _sloshCfg.TankHeightZ;
                for (int i = 0; i < n; i++) heights[i] = rest + Gauss(_cfg.LevelSensorNoiseM);
                bundle.HasLevels = true;
                bundle.Levels = new LevelSample { StampSeconds = simTime, Heights = heights };
            }

            if (_cfg.EnableVehicleCan)
            {
                bundle.HasVehicleCan = true;
                bundle.VehicleCan = new VehicleCanSample
                {
                    StampSeconds = simTime,
                    SpeedMetersPerSecond = v.Speed,
                    SteerAngleRadians = 0.0, // wired to driver input by the loop later
                    Throttle = 0.0,
                    Brake = 0.0
                };
            }

            return bundle;
        }

        private double Gauss(double sigma)
            => (_cfg.Enabled && sigma > 0.0) ? _noise.NextGaussian(0.0, sigma) : 0.0;

        private Double3 Noisy(Double3 v, double sigma)
            => new Double3(v.X + Gauss(sigma), v.Y + Gauss(sigma), v.Z + Gauss(sigma));

        private static Double3 RandomVec(IRandomSource r, double scale)
            => new Double3(r.NextGaussian(0, scale), r.NextGaussian(0, scale), r.NextGaussian(0, scale));

        private double Encoder(double angle)
        {
            double a = angle + Gauss(_cfg.EncoderNoiseRad);
            if (_cfg.Enabled && _cfg.EncoderQuantizationRad > 0.0)
                a = Math.Round(a / _cfg.EncoderQuantizationRad) * _cfg.EncoderQuantizationRad;
            return a;
        }
    }
}
