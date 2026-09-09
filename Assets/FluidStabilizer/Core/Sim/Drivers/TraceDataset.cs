using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FluidStabilizer.Core.Sim.Drivers
{
    // Explicit, versioned TSV membership. No inference from labels or hindsight artifacts.
    public static class TraceDataset
    {
        public static List<DriverTrace> Load(string manifest, string traceDirectory, string split)
        {
            if (split != "train" && split != "validation" && split != "final")
                throw new ArgumentException("split must be train, validation or final");
            var all = new List<DriverTrace>();
            string manifestHash = ConfigSnapshot.FileHash(manifest);
            foreach (string line in File.ReadLines(manifest))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("split\t")) continue;
                string[] v = line.Split('\t');
                if (v.Length != 4) throw new InvalidDataException("Expected split, file, sourceSha256, history: " + line);
                if (v[0] != "train" && v[0] != "validation" && v[0] != "final")
                    throw new InvalidDataException("Unknown split: " + v[0]);
                if (Path.GetFileName(v[1]) != v[1]) throw new InvalidDataException("Trace must be a filename: " + v[1]);
                DriverTrace t = DriverTrace.LoadFromFile(Path.Combine(traceDirectory, v[1]));
                if (t.SourceSha256 != v[2]) throw new InvalidDataException("Trace changed: " + v[1]);
                if (t.Count == 0) throw new InvalidDataException("Empty trace: " + v[1]);
                t.DatasetSplit = v[0];
                t.DatasetHistory = v[3];
                t.DatasetManifestSha256 = manifestHash;
                if (v[0] == "final" && v[3] != "unseen")
                    throw new InvalidDataException("Final evaluation requires unseen history: " + v[1]);
                all.Add(t);
            }
            ValidateUnique(all);
            var selected = all.Where(t => t.DatasetSplit == split).ToList();
            if (selected.Count == 0) throw new InvalidDataException("No traces assigned to split: " + split);
            return selected;
        }

        public static void ValidateUnique(IEnumerable<DriverTrace> traces)
        {
            var seen = new Dictionary<string, DriverTrace>();
            foreach (var t in traces)
            {
                string id = t.InputSha256;
                if (seen.TryGetValue(id, out var other))
                    throw new InvalidDataException("Duplicate drive inputs (including across splits): " + other.Label + " / " + t.Label);
                seen.Add(id, t);
            }
        }
    }
}
