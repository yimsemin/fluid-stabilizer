using System;
using System.Collections.Generic;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim.Drivers
{
    /// <summary>
    /// A realistic, band-limited highway driver: slow lane-keeping wander plus
    /// occasional <em>smooth</em> maneuvers (lane change, a highway curve held and
    /// released, easing off for traffic, a firm-but-not-emergency brake). Every
    /// maneuver is raised-cosine shaped so the resulting accelerations have no
    /// sharp edges near the slosh frequency. Deterministic given a seed.
    ///
    /// This is the counterpart to <see cref="RandomManeuverDriver"/> (kept as the
    /// aggressive / adversarial profile) for finding F1 interpretation 3.
    /// </summary>
    public sealed class HighwayDriver : IDriverInput
    {
        private enum Channel { Throttle, Brake, Steer }

        private struct Pulse
        {
            public double Start, Duration, Magnitude, Plateau; // plateau fraction of duration held flat
            public Channel Channel;
        }

        private readonly IRandomSource _rng;
        private readonly double _briskness; // 0..1
        private readonly List<Pulse> _pulses = new List<Pulse>();

        private double _throttleOu, _steerOu;
        private double _lastT;
        private double _nextEventT;

        public HighwayDriver(ulong seed, double briskness = 0.4)
        {
            _rng = new DeterministicRandom(seed).Fork("highway-driver");
            _briskness = MathD.Clamp(briskness, 0.0, 1.0);
        }

        public string Name => $"Highway(b={_briskness:F1})";

        public void Reset()
        {
            _throttleOu = 0.14;
            _steerOu = 0.0;
            _lastT = 0.0;
            _nextEventT = _rng.NextDouble(2.0, 6.0);
            _pulses.Clear();
        }

        public DriverControls Sample(double t)
        {
            double dt = t - _lastT;
            if (dt <= 0) dt = 1.0 / 600.0;
            _lastT = t;

            // Slow OU baselines (lane keeping + speed drift).
            _throttleOu += 1.0 / 4.0 * (0.14 - _throttleOu) * dt + 0.05 * _rng.NextGaussian() * Math.Sqrt(dt);
            _steerOu += 1.0 / 3.0 * (0.0 - _steerOu) * dt + 0.03 * _rng.NextGaussian() * Math.Sqrt(dt);

            // Schedule the next maneuver.
            if (t >= _nextEventT)
            {
                ScheduleEvent(t);
                double gap = _rng.NextDouble(6.0, 16.0) * (1.2 - _briskness);
                _nextEventT = t + Math.Max(3.0, gap);
            }

            double throttle = _throttleOu;
            double brake = 0.0;
            double steer = _steerOu;

            foreach (var p in _pulses)
            {
                double w = Window(t, p);
                if (w == 0.0) continue;
                switch (p.Channel)
                {
                    case Channel.Throttle: throttle += p.Magnitude * w; break;
                    case Channel.Brake: brake += p.Magnitude * w; break;
                    case Channel.Steer: steer += p.Magnitude * w; break;
                }
            }
            _pulses.RemoveAll(p => t > p.Start + p.Duration);

            return new DriverControls
            {
                Throttle = MathD.Clamp(throttle, 0, 1),
                Brake = MathD.Clamp(brake, 0, 1),
                Steer = MathD.Clamp(steer, -1, 1)
            };
        }

        private void ScheduleEvent(double t)
        {
            double kind = _rng.NextDouble();
            double sign = _rng.NextDouble() < 0.5 ? -1.0 : 1.0;

            if (kind < 0.35)
            {
                // Lane change: an S — a steer pulse then an opposite one.
                double dur = _rng.NextDouble(1.6, 2.6);
                double mag = sign * _rng.NextDouble(0.10, 0.18 + 0.12 * _briskness);
                _pulses.Add(new Pulse { Channel = Channel.Steer, Start = t, Duration = dur, Magnitude = mag });
                _pulses.Add(new Pulse { Channel = Channel.Steer, Start = t + dur, Duration = dur, Magnitude = -mag });
            }
            else if (kind < 0.6)
            {
                // Highway curve: gentle steer ramped up, held, ramped down.
                double dur = _rng.NextDouble(6.0, 12.0);
                double mag = sign * _rng.NextDouble(0.06, 0.10 + 0.08 * _briskness);
                _pulses.Add(new Pulse
                {
                    Channel = Channel.Steer, Start = t, Duration = dur, Magnitude = mag,
                    Plateau = 0.6
                });
            }
            else if (kind < 0.85)
            {
                // Ease off / light brake for traffic.
                double dur = _rng.NextDouble(3.0, 6.0);
                _pulses.Add(new Pulse
                {
                    Channel = Channel.Throttle, Start = t, Duration = dur,
                    Magnitude = -_rng.NextDouble(0.06, 0.12), Plateau = 0.4
                });
                _pulses.Add(new Pulse
                {
                    Channel = Channel.Brake, Start = t + 0.3 * dur, Duration = 0.6 * dur,
                    Magnitude = _rng.NextDouble(0.12, 0.25 + 0.2 * _briskness), Plateau = 0.3
                });
            }
            else
            {
                // Firmer (still not emergency) brake — the transient hard case.
                double dur = _rng.NextDouble(2.0, 3.5);
                _pulses.Add(new Pulse
                {
                    Channel = Channel.Brake, Start = t, Duration = dur,
                    Magnitude = _rng.NextDouble(0.4, 0.6 + 0.3 * _briskness), Plateau = 0.2
                });
                _pulses.Add(new Pulse
                {
                    Channel = Channel.Throttle, Start = t, Duration = dur, Magnitude = -0.14
                });
            }
        }

        /// <summary>
        /// Raised-cosine window: 0 → 1 over the first (1−plateau)/2 of the
        /// duration, flat at 1 for the plateau, 1 → 0 over the last part. C¹
        /// continuous, so the derivative (and thus the acceleration) has no jumps.
        /// </summary>
        private static double Window(double t, in Pulse p)
        {
            double x = (t - p.Start) / p.Duration;
            if (x <= 0.0 || x >= 1.0) return 0.0;

            double edge = 0.5 * (1.0 - p.Plateau);
            if (edge <= 1e-6) return 1.0;

            if (x < edge) return 0.5 * (1.0 - Math.Cos(Math.PI * x / edge));
            if (x > 1.0 - edge) return 0.5 * (1.0 - Math.Cos(Math.PI * (1.0 - x) / edge));
            return 1.0;
        }
    }
}
