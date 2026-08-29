using System;
using System.Globalization;
using System.IO;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Core.Sim
{
    /// <summary>
    /// The handful of knobs the owner tweaks between runs — tank fill, road,
    /// driver, episode length, slosh model — in one plain-text file
    /// (<c>experiment.txt</c> at the repo root) that <em>every</em> runner reads:
    /// the play scene, the RL training server, the evaluation, the 3-way
    /// comparison. Change the number in one place and it propagates.
    ///
    /// Everything else in <see cref="SimulationConfig"/> stays as code defaults;
    /// this profile only overrides the curated subset that changes often. The
    /// file format is the same <c># comment</c> / <c>key: value</c> style as
    /// <see cref="DriverTrace"/>, so it opens in any text editor.
    ///
    /// Recorded drives keep their own echoed setup (fill/road live in the
    /// <c>.trace.csv</c> header) — the profile does not override a replay.
    /// </summary>
    [Serializable]
    public sealed class ExperimentProfile
    {
        public const string FormatId = "fluid-stabilizer-experiment-profile/v1";

        // ---- the tank ----
        public double FillRatio = 0.62;
        public double TankHeightZ = 0.60;

        // ---- the road ----
        public bool RoadEnabled = true;
        public double RoadSeverity = 1.0;

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

        // ------------------------------------------------------------------ I/O

        public void Save(TextWriter w)
        {
            var ic = CultureInfo.InvariantCulture;
            w.WriteLine("# " + FormatId);
            w.WriteLine("# The draft operating point. Every runner reads this. Edit freely.");
            w.WriteLine("# Recorded-drive replays ignore it (they use the trace's own header).");
            w.WriteLine();
            w.WriteLine("# --- the tank ---");
            w.WriteLine("fillRatio:       " + FillRatio.ToString("0.###", ic) + "    # 0..1. 0.62 on a 0.6 m tank = 0.23 m freeboard");
            w.WriteLine("tankHeightZ:     " + TankHeightZ.ToString("0.###", ic) + "     # rim height above the floor, m");
            w.WriteLine();
            w.WriteLine("# --- the road ---");
            w.WriteLine("road:            " + (RoadEnabled ? "on" : "off") + "      # on / off");
            w.WriteLine("roadSeverity:    " + RoadSeverity.ToString("0.###", ic) + "     # 0 smooth .. 1 nominal .. >1 rough");
            w.WriteLine();
            w.WriteLine("# --- the driver (synthetic runs only) ---");
            w.WriteLine("driver:          " + Driver + "  # highway / aggressive / scripted");
            w.WriteLine("driverIntensity: " + DriverIntensity.ToString("0.###", ic) + "     # briskness (highway) or aggression (aggressive), 0..1");
            w.WriteLine();
            w.WriteLine("# --- episode ---");
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
                    case "fillratio": p.FillRatio = ParseD(val, ic, p.FillRatio); break;
                    case "tankheightz": p.TankHeightZ = ParseD(val, ic, p.TankHeightZ); break;
                    case "road": p.RoadEnabled = IsTrue(val); break;
                    case "roadseverity": p.RoadSeverity = ParseD(val, ic, p.RoadSeverity); break;
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

        private static double ParseD(string s, IFormatProvider ic, double fallback) =>
            double.TryParse(s, NumberStyles.Float, ic, out double v) ? v : fallback;

        private static bool IsTrue(string s)
        {
            s = s.ToLowerInvariant();
            return s == "on" || s == "true" || s == "1" || s == "yes";
        }
    }
}
