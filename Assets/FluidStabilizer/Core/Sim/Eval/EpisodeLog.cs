using System;
using System.Globalization;
using System.IO;

namespace FluidStabilizer.Core.Sim.Eval
{
    /// <summary>
    /// One machine-readable row per finished episode, appended to a CSV so
    /// results accumulate across runs and can be plotted / diffed later
    /// (design-review §13–§14: "log the sampled parameters of every episode").
    /// Every evaluation harness can write to the same file — the <c>source</c>
    /// column says which one.
    ///
    /// Engine-free. Deterministic given its inputs (the only non-determinism is
    /// the UTC timestamp column, which is metadata, not a result).
    ///
    /// Human-readable <c>.txt</c> reports still exist for reading at a glance —
    /// this is the "open it in a spreadsheet" companion. <c>Logs/</c> is
    /// gitignored; a curated slice can be force-added later.
    /// </summary>
    public sealed class EpisodeLog : IDisposable
    {
        public const string DefaultPath = "Logs/episodes.csv";

        private static readonly string[] Columns =
        {
            "utc", "run", "source",
            "controller", "driver", "slosh", "seed", "episodeSeconds",
            "fillRatio", "tankHeightZ", "roadEnabled", "roadSeverity", "frictionLimit",
            "spillLitres", "worstFreeboardM", "sloshEnergyIntegral",
            "meanEffortRadPerS", "torqueSatFrac", "angleLimitFrac", "physicsSteps",
        };

        private readonly TextWriter _w;
        private readonly string _run;
        private readonly bool _ownsWriter;

        /// <summary>Open (or create) <paramref name="path"/> for append; write the header if the file is new/empty.</summary>
        public EpisodeLog(string path, string run)
            : this(OpenAppend(path), run, ownsWriter: true) { }

        /// <summary>Wrap an existing writer (tests). Header is written if <paramref name="writeHeader"/>.</summary>
        public EpisodeLog(TextWriter writer, string run, bool ownsWriter = false, bool writeHeader = false)
        {
            _w = writer ?? throw new ArgumentNullException(nameof(writer));
            _run = Sanitize(run);
            _ownsWriter = ownsWriter;
            if (writeHeader) _w.WriteLine(string.Join(",", Columns));
        }

        private static TextWriter OpenAppend(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
            var w = new StreamWriter(path, append: true);
            if (fresh) w.WriteLine(string.Join(",", Columns));
            return w;
        }

        /// <summary>Append one episode. <paramref name="cfg"/> supplies the operating-point columns.</summary>
        public void Add(string source, SimulationConfig cfg, in EpisodeResult r)
        {
            var ic = CultureInfo.InvariantCulture;
            _w.WriteLine(string.Join(",", new[]
            {
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", ic),
                _run,
                Sanitize(source),
                Sanitize(r.ControllerName),
                Sanitize(r.DriverName),
                Sanitize(r.SloshModelName),
                r.Seed.ToString(ic),
                (cfg?.EpisodeSeconds ?? 0.0).ToString("0.###", ic),
                Num(cfg?.Slosh.FillRatio),
                Num(cfg?.Slosh.TankHeightZ),
                cfg != null && cfg.Road.Enabled ? "1" : "0",
                Num(cfg?.Road.Severity),
                Num(cfg?.Vehicle.FrictionLimit),
                (r.TotalOverflowVolume * 1000.0).ToString("0.####", ic),
                r.WorstFreeboard.ToString("0.#####", ic),
                r.SloshEnergyIntegral.ToString("0.###", ic),
                r.MeanAbsActionRate.ToString("0.#####", ic),
                r.TorqueSaturationFraction.ToString("0.#####", ic),
                r.AngleLimitFraction.ToString("0.#####", ic),
                r.PhysicsSteps.ToString(ic),
            }));
        }

        public void Flush() => _w.Flush();

        public void Dispose()
        {
            _w.Flush();
            if (_ownsWriter) _w.Dispose();
        }

        private static string Num(double? v) =>
            v.HasValue ? v.Value.ToString("0.#####", CultureInfo.InvariantCulture) : "";

        // Keep every field a single clean CSV cell — no quoting rules to remember.
        private static string Sanitize(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
