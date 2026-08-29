using System;
using System.Collections.Generic;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>
    /// Road excitation for one physics step: the disturbance the truck's wheels
    /// see, before the suspension filters it (design-review §4, §5 — road with
    /// grade, bank and roughness is a v1 must-model).
    /// </summary>
    public struct RoadInput
    {
        /// <summary>Road pitch, rad (uphill positive). Adds a gravity component and a body-pitch trim.</summary>
        public double Grade;

        /// <summary>Road cross-slope / curve superelevation, rad.</summary>
        public double Bank;

        /// <summary>
        /// Horizontal curvature of the road centreline, rad/m (left turn positive).
        /// The vehicle heading follows <c>Curvature · speed</c>; the driver's steer
        /// is a correction on top. Zero on a straight road / when the road is off.
        /// </summary>
        public double Curvature;

        /// <summary>
        /// Vertical displacement of the road surface under the wheels, m. The
        /// suspension (a heave spring-mass-damper in <see cref="VehicleModel"/>)
        /// turns this into body vertical motion — so a sharp bump becomes a
        /// filtered jolt at the tank, not an instantaneous acceleration spike.
        /// </summary>
        public double SurfaceHeight;
    }

    /// <summary>
    /// Deterministic road profile as a function of distance travelled. Three
    /// superposed parts:
    /// <list type="bullet">
    ///   <item>slow <b>hills</b> → a sustained, slowly varying grade (gravity
    ///     coupling + a body-pitch trim);</item>
    ///   <item>slow <b>bank</b> → curve superelevation / cross-slope;</item>
    ///   <item><b>roughness</b> (a red-spectrum sum of sinusoids, ~0.5–10 m
    ///     wavelength) plus discrete <b>events</b> (expansion joints, potholes,
    ///     stray rocks, curbs) as a Poisson process along the road.</item>
    /// </list>
    /// Everything is a pure function of the seeded parameters and the distance
    /// <c>s</c>, so an episode is bit-reproducible. All-zero output when
    /// <see cref="RoadConfig.Enabled"/> is false — the flat-road v1 default.
    /// </summary>
    public sealed class RoadModel
    {
        private const double TwoPi = 2.0 * Math.PI;

        private readonly RoadConfig _cfg;

        private bool _enabled;
        private double _gradeK, _gradePhase1, _gradePhase2;
        private double _bankK, _bankPhase;
        private double _curveK1, _curveK2, _curvePhase1, _curvePhase2;
        private double[] _roughK = Array.Empty<double>();
        private double[] _roughPhase = Array.Empty<double>();
        private double[] _roughAmp = Array.Empty<double>();

        private IRandomSource _eventRng;
        private readonly List<Event> _events = new List<Event>();
        private double _eventsGeneratedTo;

        private struct Event { public double Pos, Height, HalfLength; }

        public RoadModel(RoadConfig cfg)
        {
            _cfg = cfg ?? new RoadConfig();
        }

        /// <summary>Re-seed for a new episode. Must be called before <see cref="Sample"/>.</summary>
        public void Reset(IRandomSource rng)
        {
            _events.Clear();
            _eventsGeneratedTo = -50.0;
            _enabled = _cfg.Enabled;
            if (!_enabled) return;

            IRandomSource r = rng;

            _gradeK = TwoPi / Math.Max(1.0, _cfg.HillWavelengthM);
            _gradePhase1 = r.NextDouble(0.0, TwoPi);
            _gradePhase2 = r.NextDouble(0.0, TwoPi);

            _bankK = TwoPi / Math.Max(1.0, _cfg.BankWavelengthM);
            _bankPhase = r.NextDouble(0.0, TwoPi);

            // Roughness: n sinusoids, wavelengths geometric from long → short.
            // Amplitude ∝ 1/k (a red-ish ride spectrum), then the whole set is
            // rescaled so the RMS of the superposition equals RoughnessRmsM.
            const int n = 6;
            double kLong = TwoPi / Math.Max(0.05, _cfg.RoughnessLongWavelengthM);
            double kShort = TwoPi / Math.Max(0.05, _cfg.RoughnessShortWavelengthM);
            _roughK = new double[n];
            _roughPhase = new double[n];
            _roughAmp = new double[n];
            double sumSq = 0.0;
            for (int i = 0; i < n; i++)
            {
                double f = n == 1 ? 0.0 : i / (double)(n - 1);
                double k = kLong * Math.Pow(kShort / kLong, f);
                double a = 1.0 / k;
                _roughK[i] = k;
                _roughPhase[i] = r.NextDouble(0.0, TwoPi);
                _roughAmp[i] = a;
                sumSq += a * a;
            }
            // RMS of Σ aᵢ sin(·) with independent phases = sqrt(½ Σ aᵢ²).
            double rms = Math.Sqrt(0.5 * sumSq);
            double target = Math.Max(0.0, _cfg.RoughnessRmsM) * Math.Max(0.0, _cfg.Severity);
            double gain = rms > 1e-12 ? target / rms : 0.0;
            for (int i = 0; i < n; i++) _roughAmp[i] *= gain;

            _eventRng = r.Fork("events");

            // Horizontal curve (drawn last so grade/bank/roughness/event streams
            // above are byte-identical to before this channel existed).
            _curveK1 = TwoPi / Math.Max(1.0, _cfg.CurvatureWavelengthM);
            _curveK2 = 2.7 * _curveK1;
            _curvePhase1 = r.NextDouble(0.0, TwoPi);
            _curvePhase2 = r.NextDouble(0.0, TwoPi);
        }

        /// <summary>Centreline curvature at distance <paramref name="s"/> (rad/m, left positive).</summary>
        private double CurvatureAt(double s)
        {
            if (!_enabled) return 0.0;
            return _cfg.CurvatureAmplitudeRadPerM *
                   (Math.Sin(_curveK1 * s + _curvePhase1) + 0.4 * Math.Sin(_curveK2 * s + _curvePhase2)) / 1.4;
        }

        /// <summary>The road under the wheels at distance <paramref name="s"/> metres.</summary>
        public RoadInput Sample(double s)
        {
            if (!_enabled) return default;

            double grade = _cfg.GradeAmplitudeRad *
                           (0.8 * Math.Sin(_gradeK * s + _gradePhase1) +
                            0.3 * Math.Sin(2.3 * _gradeK * s + _gradePhase2)) / 1.1;

            double curvature = CurvatureAt(s);
            double superelevation = MathD.ClampAbs(_cfg.BankPerCurvature * curvature, 0.08);
            double bank = superelevation + _cfg.BankAmplitudeRad * Math.Sin(_bankK * s + _bankPhase);

            double z = 0.0;
            for (int i = 0; i < _roughK.Length; i++)
                z += _roughAmp[i] * Math.Sin(_roughK[i] * s + _roughPhase[i]);

            GenerateEventsUpTo(s + 25.0);
            for (int i = 0; i < _events.Count; i++)
            {
                double ds = s - _events[i].Pos;
                double hl = _events[i].HalfLength;
                if (ds < -hl || ds > hl) continue;
                // Raised-cosine bump/pothole: C¹-continuous, zero at the ends.
                z += _events[i].Height * 0.5 * (1.0 + Math.Cos(Math.PI * ds / hl));
            }
            // Drop events well behind the truck so the list stays short.
            if (_events.Count > 0 && _events[0].Pos < s - 40.0)
                _events.RemoveAll(e => e.Pos < s - 40.0);

            return new RoadInput { Grade = grade, Bank = bank, SurfaceHeight = z, Curvature = curvature };
        }

        private void GenerateEventsUpTo(double sMax)
        {
            if (_cfg.EventsPerKm <= 0.0 || _cfg.EventHeightM <= 0.0) { _eventsGeneratedTo = sMax; return; }
            double meanGapM = 1000.0 / _cfg.EventsPerKm;
            double severity = Math.Max(0.0, _cfg.Severity);

            while (_eventsGeneratedTo < sMax)
            {
                // Exponential inter-arrival (Poisson process along distance).
                double u = 1.0 - _eventRng.NextDouble();      // (0, 1]
                double gap = -meanGapM * Math.Log(u);
                double pos = _eventsGeneratedTo + Math.Max(0.5, gap);
                _eventsGeneratedTo = pos;

                double sign = _eventRng.NextDouble() < 0.5 ? -1.0 : 1.0; // pothole vs bump
                double height = sign * _cfg.EventHeightM * severity * _eventRng.NextDouble(0.5, 1.5);
                double halfLen = 0.5 * _cfg.EventLengthM * _eventRng.NextDouble(0.7, 1.6);
                _events.Add(new Event { Pos = pos, Height = height, HalfLength = Math.Max(0.05, halfLen) });
            }
        }
    }
}
