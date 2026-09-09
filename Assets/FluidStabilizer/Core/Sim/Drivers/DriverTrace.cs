using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Core.Sim.Drivers
{
    /// <summary>
    /// A recorded driver-input time series: the exact <see cref="DriverControls"/>
    /// produced at every physics step of one drive, plus the setup needed to
    /// reproduce that drive (seed, step size, fill level, road, slosh model).
    /// Replaying it through <see cref="RecordedTraceDriver"/> over the same config
    /// reproduces the episode under the same code; legacy missing settings remain assumptions.
    ///
    /// This is the "recorded human drive" of design-review §17 / project-goals ③:
    /// the real disturbance data for training <em>and</em> the held-out evaluation
    /// set for the three-way comparison. Everything else in the sim is
    /// deterministic given the seed, so a trace + its stored setup is a complete,
    /// portable definition of one test condition.
    /// </summary>
    public sealed class DriverTrace
    {
        public const string FormatId = "fluid-stabilizer-driver-trace/v2";
        public readonly SortedDictionary<string, string> RecordedConfig = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public string RecordedCode = "unknown";
        public string RecordedController = "unknown";
        public string SourcePath = "";
        public string SourceSha256 = "";
        public string DatasetSplit = "unassigned";
        public string DatasetManifestSha256 = "";
        public string DatasetHistory = "";

        public void CaptureConfig(SimulationConfig config, ulong seed, string sloshModel, string controller = "unknown")
        {
            RecordedConfig.Clear();
            foreach (var kv in ConfigSnapshot.Capture(config)) RecordedConfig.Add(kv.Key, kv.Value);
            Seed = seed;
            StepSeconds = config.PhysicsStepSeconds;
            FillRatio = config.Slosh.FillRatio;
            RoadEnabled = config.Road.Enabled;
            RoadSeverity = config.Road.Severity;
            SloshModel = sloshModel;
            RecordedController = controller;
            RecordedCode = Eval.RunProvenance.CodeIdentity;
        }

        public string Assumptions => string.Join(";", ConfigSnapshot.Capture(new SimulationConfig()).Keys
            .Where(k => k != "EpisodeSeconds" && !RecordedConfig.ContainsKey(k)
                && k != "PhysicsRateHz" && k != "Slosh.FillRatio" && k != "Road.Enabled" && k != "Road.Severity")
            .Select(k => "unknown-at-recording:" + k + "=" + ConfigSnapshot.Capture(ToConfig())[k]));

        // Input identity deliberately excludes filename, label, seed and physical config.
        // Copying/renaming a drive or changing its road cannot create a new holdout.
        public string InputSha256
        {
            get
            {
                var b = new StringBuilder(StepSeconds.ToString("R", CultureInfo.InvariantCulture));
                foreach (var c in Samples)
                    b.Append('\n').Append(c.Throttle.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(c.Brake.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(c.Steer.ToString("R", CultureInfo.InvariantCulture));
                return ConfigSnapshot.Hash(b.ToString());
            }
        }

        /// <summary>Human-readable name, e.g. "citycommute-3" or "hardbrake-lowfill".</summary>
        public string Label = "unnamed";

        /// <summary>Master seed the drive was recorded under (road, sensors, slosh).</summary>
        public ulong Seed;

        /// <summary>Physics step the samples are spaced at (s). Must match on replay.</summary>
        public double StepSeconds = 1.0 / 600.0;

        // ---- setup echo: enough to rebuild the SimulationConfig this ran under ----
        public double FillRatio = 0.55;
        public bool RoadEnabled;
        public double RoadSeverity = 1.0;

        /// <summary>"pendulum" or "shallow" — the slosh model in effect while recording.</summary>
        public string SloshModel = "pendulum";

        /// <summary>One entry per physics step, in order, the first being the step ending at t = StepSeconds.</summary>
        public readonly List<DriverControls> Samples = new List<DriverControls>();

        public int Count => Samples.Count;
        public double DurationSeconds => Samples.Count * StepSeconds;

        public void Add(in DriverControls c) => Samples.Add(c);

        /// <summary>
        /// A <see cref="SimulationConfig"/> matching the recorded setup. Episode
        /// length is set to cover the trace exactly. The caller still picks the
        /// controller and passes <see cref="MakeSloshModel"/> to the loop.
        /// </summary>
        public SimulationConfig ToConfig()
        {
            var c = ConfigSnapshot.Restore(RecordedConfig);
            // Legacy header values are known; remaining defaults are assumptions, never persisted as facts.
            if (!RecordedConfig.ContainsKey("PhysicsRateHz")) c.PhysicsRateHz = 1.0 / StepSeconds;
            if (!RecordedConfig.ContainsKey("Slosh.FillRatio")) c.Slosh.FillRatio = FillRatio;
            if (!RecordedConfig.ContainsKey("Road.Enabled")) c.Road.Enabled = RoadEnabled;
            if (!RecordedConfig.ContainsKey("Road.Severity")) c.Road.Severity = RoadSeverity;
            c.EpisodeSeconds = DurationSeconds;
            c.Validate();
            return c;
        }

        /// <summary>The slosh model named in the trace (defaults to the pendulum ROM).</summary>
        public ISloshModel MakeSloshModel() =>
            SloshModel == "shallow" ? new ShallowWaterSloshModel() : (ISloshModel)new PendulumSloshModel();

        // ------------------------------------------------------------------ I/O
        // A small text format anyone can open: "# key: value" header lines, then a
        // "throttle,brake,steer" column line, then one CSV row per physics step.

        public void Save(TextWriter w)
        {
            var ic = CultureInfo.InvariantCulture;
            w.WriteLine("# " + FormatId);
            w.WriteLine("# label: " + Label);
            w.WriteLine("# seed: " + Seed.ToString(ic));
            w.WriteLine("# stepSeconds: " + StepSeconds.ToString("R", ic));
            w.WriteLine("# fillRatio: " + FillRatio.ToString("R", ic));
            w.WriteLine("# roadEnabled: " + (RoadEnabled ? "1" : "0"));
            w.WriteLine("# roadSeverity: " + RoadSeverity.ToString("R", ic));
            w.WriteLine("# sloshModel: " + SloshModel);
            w.WriteLine("# recordedCode: " + RecordedCode);
            w.WriteLine("# recordedController: " + RecordedController);
            foreach (var kv in RecordedConfig) w.WriteLine("# config." + kv.Key + ": " + kv.Value);
            w.WriteLine("# samples: " + Samples.Count.ToString(ic));
            w.WriteLine("throttle,brake,steer");
            foreach (var c in Samples)
                w.WriteLine(c.Throttle.ToString("R", ic) + "," +
                            c.Brake.ToString("R", ic) + "," +
                            c.Steer.ToString("R", ic));
        }

        public static DriverTrace Load(TextReader r)
        {
            var ic = CultureInfo.InvariantCulture;
            var trace = new DriverTrace();
            bool inRows = false;
            string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                if (line[0] == '#') { ParseHeader(line, trace, ic); continue; }
                if (!inRows) { inRows = true; continue; } // the "throttle,brake,steer" line
                int a = line.IndexOf(',');
                int b = line.IndexOf(',', a + 1);
                trace.Samples.Add(new DriverControls
                {
                    Throttle = double.Parse(line.Substring(0, a), ic),
                    Brake = double.Parse(line.Substring(a + 1, b - a - 1), ic),
                    Steer = double.Parse(line.Substring(b + 1), ic)
                });
            }
            return trace;
        }

        private static void ParseHeader(string line, DriverTrace t, IFormatProvider ic)
        {
            int c = line.IndexOf(':');
            if (c < 0) return;
            string key = line.Substring(1, c - 1).Trim();
            string val = line.Substring(c + 1).Trim();
            if (key.StartsWith("config.", StringComparison.Ordinal))
            {
                t.RecordedConfig.Add(key.Substring(7), val);
                return;
            }
            switch (key)
            {
                case "recordedCode": t.RecordedCode = val; break;
                case "recordedController": t.RecordedController = val; break;
                case "label": t.Label = val; break;
                case "seed": t.Seed = ulong.Parse(val, ic); break;
                case "stepSeconds": t.StepSeconds = double.Parse(val, ic); break;
                case "fillRatio": t.FillRatio = double.Parse(val, ic); break;
                case "roadEnabled": t.RoadEnabled = val == "1" || val == "true"; break;
                case "roadSeverity": t.RoadSeverity = double.Parse(val, ic); break;
                case "sloshModel": t.SloshModel = val; break;
            }
        }

        public void SaveToFile(string path)
        {
            using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(f)) Save(w);
        }

        public static DriverTrace LoadFromFile(string path)
        {
            using var r = new StreamReader(path);
            var trace = Load(r);
            trace.SourcePath = Path.GetFullPath(path);
            trace.SourceSha256 = ConfigSnapshot.FileHash(path);
            return trace;
        }
    }

    /// <summary>
    /// Wraps another driver, passing every <see cref="DriverControls"/> straight
    /// through while appending it to a <see cref="DriverTrace"/>. The sim core is
    /// unaware; the play scene uses it to record a human drive.
    /// </summary>
    public sealed class RecordingDriver : IDriverInput
    {
        private readonly IDriverInput _inner;

        public DriverTrace Trace { get; }

        public RecordingDriver(IDriverInput inner, DriverTrace trace)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Trace = trace ?? throw new ArgumentNullException(nameof(trace));
        }

        public string Name => _inner.Name;

        public void Reset()
        {
            _inner.Reset();
            Trace.Samples.Clear();
        }

        public DriverControls Sample(double simTimeSeconds)
        {
            DriverControls c = _inner.Sample(simTimeSeconds);
            Trace.Samples.Add(c);
            return c;
        }
    }

    /// <summary>
    /// Replays a <see cref="DriverTrace"/>: the sample recorded for physics step
    /// k is returned for the step ending at t ≈ (k+1)·StepSeconds. Holds the last
    /// sample once the trace runs out. Deterministic; no RNG.
    /// </summary>
    public sealed class RecordedTraceDriver : IDriverInput
    {
        private readonly DriverTrace _trace;

        public RecordedTraceDriver(DriverTrace trace)
        {
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        }

        public string Name => "Recorded(" + _trace.Label + ")";

        public void Reset() { }

        public DriverControls Sample(double simTimeSeconds)
        {
            int n = _trace.Samples.Count;
            if (n == 0) return DriverControls.Neutral;

            int idx = (int)Math.Round(simTimeSeconds / _trace.StepSeconds) - 1;
            if (idx < 0) idx = 0;
            else if (idx >= n) idx = n - 1;
            return _trace.Samples[idx];
        }
    }
}
