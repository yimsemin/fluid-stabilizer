using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim.Drivers
{
    /// <summary>
    /// A stochastic but deterministic-given-seed driver: throttle, brake and
    /// steering each follow an Ornstein–Uhlenbeck process, punctuated by
    /// occasional aggressive events (hard brake, sharp turn). Stands in for
    /// unpredictable human input for evaluation and early training. NOT a
    /// substitute for recorded human traces (design-review §17) — those come
    /// later as a held-out set.
    /// </summary>
    public sealed class RandomManeuverDriver : IDriverInput
    {
        private readonly double _aggression;      // 0..1
        private readonly IRandomSource _rng;
        private double _throttle, _brake, _steer;
        private double _eventUntil;
        private double _eventThrottle, _eventBrake, _eventSteer;
        private double _lastT;

        public RandomManeuverDriver(ulong seed, double aggression = 0.5)
        {
            _rng = new DeterministicRandom(seed).Fork("driver");
            _aggression = MathD.Clamp(aggression, 0.0, 1.0);
        }

        public string Name => $"RandomManeuver(a={_aggression:F1})";

        public void Reset()
        {
            _throttle = 0.15;
            _brake = 0.0;
            _steer = 0.0;
            _eventUntil = 0.0;
            _lastT = 0.0;
        }

        public DriverControls Sample(double t)
        {
            double dt = t - _lastT;
            if (dt <= 0) dt = 1.0 / 600.0;
            _lastT = t;

            // OU relaxation toward a mild cruise.
            double theta = 1.6, sigma = 0.25 + 0.5 * _aggression;
            _throttle += theta * (0.15 - _throttle) * dt + sigma * _rng.NextGaussian() * Math.Sqrt(dt);
            _brake += theta * (0.0 - _brake) * dt + sigma * _rng.NextGaussian() * Math.Sqrt(dt);
            _steer += theta * (0.0 - _steer) * dt + sigma * _rng.NextGaussian() * Math.Sqrt(dt);

            // Occasional aggressive event (short — a swerve or a stab of the brake).
            double eventRate = 0.04 + 0.16 * _aggression; // per second
            if (t > _eventUntil && _rng.NextDouble() < eventRate * dt)
            {
                _eventUntil = t + _rng.NextDouble(0.3, 1.0);
                int kind = _rng.NextInt(0, 3);
                _eventThrottle = kind == 0 ? _rng.NextDouble(0.5, 0.9) : 0.0;
                _eventBrake = kind == 1 ? _rng.NextDouble(0.5, 0.9) : 0.0;
                _eventSteer = kind == 2 ? (_rng.NextDouble() < 0.5 ? -1 : 1) * _rng.NextDouble(0.4, 0.75) : 0.0;
            }

            double thr = _throttle, brk = _brake, str = _steer;
            if (t <= _eventUntil)
            {
                thr = _eventThrottle;
                brk = _eventBrake;
                str = _eventSteer;
            }

            return new DriverControls
            {
                Throttle = MathD.Clamp(thr, 0, 1),
                Brake = MathD.Clamp(brk, 0, 1),
                Steer = MathD.Clamp(str, -1, 1)
            };
        }
    }
}
