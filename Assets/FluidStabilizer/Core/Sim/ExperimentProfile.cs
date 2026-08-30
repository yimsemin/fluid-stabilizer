using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>
    /// The handful of knobs the owner tweaks between runs — tank fill, road
    /// character, driver, episode length, slosh model — in one plain-text file
    /// (<c>experiment.txt</c> at the repo root) that <em>every</em> runner reads:
    /// the play scene, the RL training server, the evaluation, the 3-way
    /// comparison. Change a number in one place and it propagates. The in-scene
    /// control panel (backquote key) edits this live; a few road presets bundle
    /// sensible values.
    ///
    /// Everything else in <see cref="SimulationConfig"/> stays as code defaults.
    /// Recorded drives keep their own echoed setup (fill/road live in the
    /// <c>.trace.csv</c> header) — the profile does not override a replay.
    /// </summary>
    [Serializable]
    public sealed class ExperimentProfile
    {
        public const string FormatId = "fluid-stabilizer-experiment-profile/v1";

        /// <summary>Name of the last-applied preset (informational; free-form once edited).</summary>
        public string Preset = "일반 고속도로";

        // ---- the tank ----
        public double FillRatio = 0.62;
        public double TankHeightZ = 0.60;

        // ---- the road ----
        public bool RoadEnabled = true;
        public double RoadSeverity = 1.0;   // roughness + potholes;  0 smooth .. 1 nominal .. >1 rough
        public double RoadCurve = 1.0;      // × the nominal curvature (windiness)
        public double RoadHills = 1.0;      // × the nominal grade (up/down steepness)
        public double Grip = 1.0;           // × tyre friction/brake/accel.  1 dry, ~0.6 wet, ~0.35 snow

        // ---- the driver (synthetic runs only; a replayed trace uses its own inputs) ----
        /// <summary>"highway", "aggressive", or "scripted".</summary>
        public string Driver = "highway";
        /// <summary>Briskness for "highway", aggression for "aggressive"; 0..1.</summary>
        public double DriverIntensity = 0.6;

        // ---- episode ----
        public double EpisodeSeconds = 40.0;
        /// <summary>"pendulum" or "shallow".</summary>
        public string SloshModel = "pendulum";
        public ulong Seed = 1;

        public static ExperimentProfile Defaults() => new ExperimentProfile();

        /// <summary>A fresh <see cref="SimulationConfig"/> with this profile applied.</summary>
        public SimulationConfig ToConfig(double? episodeSecondsOverride = null)
        {
            var c = new SimulationConfig
            {
                EpisodeSeconds = episodeSecondsOverride ?? EpisodeSeconds
            };

            c.Slosh.FillRatio = FillRatio;
            c.Slosh.TankHeightZ = TankHeightZ;

            c.Road.Enabled = RoadEnabled;
            c.Road.Severity = RoadSeverity;
            c.Road.CurvatureAmplitudeRadPerM *= Math.Max(0.0, RoadCurve);
            c.Road.GradeAmplitudeRad *= Math.Max(0.0, RoadHills);

            double g = MathClamp(Grip, 0.15, 1.5);
            c.Vehicle.FrictionLimit *= g;
            c.Vehicle.MaxBrake *= g;
            c.Vehicle.MaxAccel *= g;

            return c;
        }

        public IDriverInput MakeDriver(ulong seed)
        {
            switch ((Driver ?? "highway").Trim().ToLowerInvariant())
            {
                case "aggressive": return new RandomManeuverDriver(seed, DriverIntensity);
                case "scripted": return new ScriptedManeuverDriver();
                default: return new HighwayDriver(seed, DriverIntensity);
            }
        }

        public ISloshModel MakeSlosh() =>
            (SloshModel ?? "pendulum").Trim().ToLowerInvariant() == "shallow"
                ? new ShallowWaterSloshModel()
                : (ISloshModel)new PendulumSloshModel();

        // ---------------------------------------------------------------- presets

        /// <summary>Named road/tank bundles for the control panel. Ordered.</summary>
        public static readonly List<(string name, Action<ExperimentProfile> apply)> Presets =
            new List<(string, Action<ExperimentProfile>)>
            {
                ("평지 · 도로 없음", p => { p.RoadEnabled = false; p.RoadSeverity = 1; p.RoadCurve = 1; p.RoadHills = 1; p.Grip = 1; p.FillRatio = 0.55; }),
                ("일반 고속도로",     p => { p.RoadEnabled = true;  p.RoadSeverity = 0.5; p.RoadCurve = 0.8; p.RoadHills = 0.7; p.Grip = 1.0;  p.FillRatio = 0.62; }),
                ("비포장 도로",       p => { p.RoadEnabled = true;  p.RoadSeverity = 1.8; p.RoadCurve = 1.1; p.RoadHills = 1.1; p.Grip = 0.9;  p.FillRatio = 0.62; }),
                ("굽은 길",           p => { p.RoadEnabled = true;  p.RoadSeverity = 0.6; p.RoadCurve = 2.4; p.RoadHills = 0.8; p.Grip = 1.0;  p.FillRatio = 0.62; }),
                ("급경사 길",         p => { p.RoadEnabled = true;  p.RoadSeverity = 0.6; p.RoadCurve = 0.9; p.RoadHills = 2.6; p.Grip = 1.0;  p.FillRatio = 0.62; }),
                ("빗길 (미끄러움)",   p => { p.RoadEnabled = true;  p.RoadSeverity = 0.8; p.RoadCurve = 1.0; p.RoadHills = 0.9; p.Grip = 0.6;  p.FillRatio = 0.62; }),
                ("눈길 (매우 미끄러움)", p => { p.RoadEnabled = true; p.RoadSeverity = 0.7; p.RoadCurve = 1.0; p.RoadHills = 0.9; p.Grip = 0.35; p.FillRatio = 0.62; }),
            };

        /// <summary>Apply the named preset (road/tank fields only; driver/slosh/seed untouched). Returns false if unknown.</summary>
        public bool ApplyPreset(string name)
        {
            foreach (var (n, apply) in Presets)
                if (n == name) { apply(this); Preset = n; return true; }
            return false;
        }

        // ------------------------------------------------------------------ I/O

        public void Save(TextWriter w)
        {
            var ic = CultureInfo.InvariantCulture;
            w.WriteLine("# " + FormatId);
            w.WriteLine("# 초안 운영점. 모든 러너가 이 파일을 읽는다. 자유롭게 편집.");
            w.WriteLine("# 인게임 컨트롤 패널(백틱 키)로도 편집 가능. 녹화 주행 재생은 이 파일을 무시.");
            w.WriteLine();
            w.WriteLine("preset:          " + Preset + "   # 마지막 적용 프리셋 (참고용)");
            w.WriteLine();
            w.WriteLine("# --- 탱크 ---");
            w.WriteLine("fillRatio:       " + FillRatio.ToString("0.###", ic) + "    # 0..1. 0.6 m 탱크에 0.62 = freeboard 0.23 m");
            w.WriteLine("tankHeightZ:     " + TankHeightZ.ToString("0.###", ic) + "     # rim 높이, m");
            w.WriteLine();
            w.WriteLine("# --- 도로 ---");
            w.WriteLine("road:            " + (RoadEnabled ? "on" : "off"));
            w.WriteLine("roadSeverity:    " + RoadSeverity.ToString("0.###", ic) + "     # 거칠기+포트홀. 0 매끈 .. 1 보통 .. >1 거침");
            w.WriteLine("roadCurve:       " + RoadCurve.ToString("0.###", ic) + "     # × 곡률 (굽은 정도)");
            w.WriteLine("roadHills:       " + RoadHills.ToString("0.###", ic) + "     # × 경사 (오르내림)");
            w.WriteLine("grip:            " + Grip.ToString("0.###", ic) + "     # × 타이어 마찰. 1 마른길, ~0.6 빗길, ~0.35 눈길");
            w.WriteLine();
            w.WriteLine("# --- 운전자 (합성 실행만; 재생은 트레이스 입력 사용) ---");
            w.WriteLine("driver:          " + Driver + "  # highway / aggressive / scripted");
            w.WriteLine("driverIntensity: " + DriverIntensity.ToString("0.###", ic) + "     # 0..1");
            w.WriteLine();
            w.WriteLine("# --- 에피소드 ---");
            w.WriteLine("episodeSeconds:  " + EpisodeSeconds.ToString("0.###", ic));
            w.WriteLine("sloshModel:      " + SloshModel + "  # pendulum / shallow");
            w.WriteLine("seed:            " + Seed.ToString(ic));
        }

        public void SaveToFile(string path)
        {
            using (var w = new StreamWriter(path, false)) Save(w);
        }

        public static ExperimentProfile Load(TextReader r)
        {
            var ic = CultureInfo.InvariantCulture;
            var p = new ExperimentProfile();
            string line;
            while ((line = r.ReadLine()) != null)
            {
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;

                int colon = line.IndexOf(':');
                if (colon < 0) colon = line.IndexOf('=');
                if (colon < 0) continue;

                string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                string val = line.Substring(colon + 1).Trim();
                if (val.Length == 0) continue;

                switch (key)
                {
                    case "preset": p.Preset = val; break;
                    case "fillratio": p.FillRatio = ParseD(val, ic, p.FillRatio); break;
                    case "tankheightz": p.TankHeightZ = ParseD(val, ic, p.TankHeightZ); break;
                    case "road": p.RoadEnabled = IsTrue(val); break;
                    case "roadseverity": p.RoadSeverity = ParseD(val, ic, p.RoadSeverity); break;
                    case "roadcurve": p.RoadCurve = ParseD(val, ic, p.RoadCurve); break;
                    case "roadhills": p.RoadHills = ParseD(val, ic, p.RoadHills); break;
                    case "grip": p.Grip = ParseD(val, ic, p.Grip); break;
                    case "driver": p.Driver = val.ToLowerInvariant(); break;
                    case "driverintensity": p.DriverIntensity = ParseD(val, ic, p.DriverIntensity); break;
                    case "episodeseconds": p.EpisodeSeconds = ParseD(val, ic, p.EpisodeSeconds); break;
                    case "sloshmodel": p.SloshModel = val.ToLowerInvariant(); break;
                    case "seed": p.Seed = ulong.TryParse(val, out ulong s) ? s : p.Seed; break;
                }
            }
            return p;
        }

        /// <summary>Load the profile, or return defaults (and write them to <paramref name="path"/>) if the file is missing/unreadable.</summary>
        public static ExperimentProfile LoadFromFileOrDefaults(string path)
        {
            try
            {
                if (File.Exists(path))
                    using (var r = new StreamReader(path)) return Load(r);
            }
            catch { /* fall through to defaults */ }

            var d = Defaults();
            try { d.SaveToFile(path); } catch { /* best effort */ }
            return d;
        }

        public ExperimentProfile Clone()
        {
            using (var w = new StringWriter())
            {
                Save(w);
                using (var r = new StringReader(w.ToString())) return Load(r);
            }
        }

        private static double ParseD(string s, IFormatProvider ic, double fallback) =>
            double.TryParse(s, NumberStyles.Float, ic, out double v) ? v : fallback;

        private static double MathClamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        private static bool IsTrue(string s)
        {
            s = s.ToLowerInvariant();
            return s == "on" || s == "true" || s == "1" || s == "yes";
        }
    }
}
