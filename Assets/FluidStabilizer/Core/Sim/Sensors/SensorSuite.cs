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
            in TankKinematicState tank)
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

            // --- Load cells: 4 corners. v1 = static weight split + noise.
            //     (Slosh CoM redistribution wired in when the pendulum model lands.)
            double liquidMass = _sloshCfg.FillRatio * _sloshCfg.TankLengthX * _sloshCfg.TankWidthY
                                * _sloshCfg.TankHeightZ * _sloshCfg.LiquidDensity;
            double totalWeight = (_sloshCfg.DryMass + liquidMass) * MathD.StandardGravity;
            double perCell = totalWeight / 4.0;
            bundle.LoadCells = new LoadCellSample
            {
                StampSeconds = simTime,
                Forces = new[]
                {
                    perCell + Gauss(_cfg.LoadCellNoiseN),
                    perCell + Gauss(_cfg.LoadCellNoiseN),
                    perCell + Gauss(_cfg.LoadCellNoiseN),
                    perCell + Gauss(_cfg.LoadCellNoiseN)
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
