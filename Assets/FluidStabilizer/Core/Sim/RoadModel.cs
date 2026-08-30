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
        /// Vertical displacement of the road surface under the wheels, m —
        /// the <b>average</b> of the two wheel tracks. The suspension (a heave
        /// spring-mass-damper in <see cref="VehicleModel"/>) turns this into body
        /// vertical motion, so a sharp bump becomes a filtered jolt at the tank.
        /// </summary>
        public double SurfaceHeight;

        /// <summary>Surface displacement under the left / right wheel track, m.
        /// They share the long undulations but decorrelate at short wavelength,
        /// and a stray rock or pothole often hits only one side — so the
        /// difference rolls the truck (a real disturbance a rigid tank cannot
        /// reject). Equal to <see cref="SurfaceHeight"/> when the road is off.</summary>
        public double SurfaceHeightLeft;
        public double SurfaceHeightRight;
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

        // Left/right wheel-track differential roughness: a small red sum with its
        // own phases, added to one track and subtracted from the other.
        private double[] _diffK = Array.Empty<double>();
        private double[] _diffPhase = Array.Empty<double>();
        private double[] _diffAmp = Array.Empty<double>();

        private IRandomSource _eventRng;
        private IRandomSource _sideRng;   // per-event L/R/both, drawn in strict event order (own stream)
        private readonly List<Event> _events = new List<Event>();
        private double _eventsGeneratedTo;

        // Side: 0 = both wheels, +1 = left only, -1 = right only.
        private struct Event { public double Pos, Height, HalfLength; public int Side; }

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

            // ---- left/right wheel-track differential (own forked stream, so the
            //      average road above stays byte-identical to before) ----
            IRandomSource wt = r.Fork("wheeltracks");
            _sideRng = wt.Fork("eventsides");
            const int nd = 3;
            double dkLong = TwoPi / Math.Max(0.05, 0.6 * _cfg.RoughnessLongWavelengthM); // shorter than the shared set
            double dkShort = TwoPi / Math.Max(0.05, _cfg.RoughnessShortWavelengthM);
            _diffK = new double[nd];
            _diffPhase = new double[nd];
            _diffAmp = new double[nd];
            double dsumSq = 0.0;
            for (int i = 0; i < nd; i++)
            {
                double f = nd == 1 ? 0.0 : i / (double)(nd - 1);
                double k = dkLong * Math.Pow(dkShort / dkLong, f);
                double a = 1.0 / k;
                _diffK[i] = k;
                _diffPhase[i] = wt.NextDouble(0.0, TwoPi);
                _diffAmp[i] = a;
                dsumSq += a * a;
            }
            double drms = Math.Sqrt(0.5 * dsumSq);
            double dtarget = Math.Max(0.0, _cfg.RoughnessRmsM) * Math.Max(0.0, _cfg.Severity)
                             * Math.Max(0.0, _cfg.WheelTrackRoughnessFrac);
            double dgain = drms > 1e-12 ? dtarget / drms : 0.0;
            for (int i = 0; i < nd; i++) _diffAmp[i] *= dgain;
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

            double zAvg = 0.0;
            for (int i = 0; i < _roughK.Length; i++)
                zAvg += _roughAmp[i] * Math.Sin(_roughK[i] * s + _roughPhase[i]);

            // Differential (left − right) roughness.
            double zDiff = 0.0;
            for (int i = 0; i < _diffK.Length; i++)
                zDiff += _diffAmp[i] * Math.Sin(_diffK[i] * s + _diffPhase[i]);

            GenerateEventsUpTo(s + 25.0);
            for (int i = 0; i < _events.Count; i++)
            {
                double ds = s - _events[i].Pos;
                double hl = _events[i].HalfLength;
                if (ds < -hl || ds > hl) continue;
                // Raised-cosine bump/pothole: C¹-continuous, zero at the ends.
                double bump = _events[i].Height * 0.5 * (1.0 + Math.Cos(Math.PI * ds / hl));
                int side = _events[i].Side;
                if (side == 0) { zAvg += bump; }
                else
                {
                    // one wheel only: half the drop reaches the average, half the difference
                    zAvg += 0.5 * bump;
                    zDiff += (side > 0 ? 0.5 : -0.5) * bump;
                }
            }
            // Drop events well behind the truck so the list stays short.
            if (_events.Count > 0 && _events[0].Pos < s - 40.0)
                _events.RemoveAll(e => e.Pos < s - 40.0);

            return new RoadInput
            {
                Grade = grade, Bank = bank, Curvature = curvature,
                SurfaceHeight = zAvg,
                SurfaceHeightLeft = zAvg + zDiff,
                SurfaceHeightRight = zAvg - zDiff,
            };
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

                // ~45% span both wheels (joints, wide potholes), else one side
                // (rocks, edge potholes). WheelTrackRoughnessFrac == 0 recovers the
                // old symmetric single-track road.
                double u2 = _sideRng.NextDouble();   // always drawn (stream stays event-order stable)
                int esideVal = _cfg.WheelTrackRoughnessFrac <= 0.0 ? 0
                             : (u2 < 0.45 ? 0 : (u2 < 0.725 ? 1 : -1));

                _events.Add(new Event { Pos = pos, Height = height, HalfLength = Math.Max(0.05, halfLen), Side = esideVal });
            }
        }
    }
}
