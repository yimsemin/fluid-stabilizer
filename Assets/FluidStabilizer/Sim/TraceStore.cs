using System.IO;
using System.Linq;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Where recorded <see cref="Core.Sim.Drivers.DriverTrace"/> files live, shared
    /// by <see cref="TraceRecorder"/> (writes them) and <see cref="ComparisonRunner"/>
    /// (replays them). A plain <c>Traces/</c> folder next to the project in the
    /// Editor; <c>persistentDataPath/traces</c> in a build.
    /// </summary>
    public static class TraceStore
    {
        public const string Extension = ".trace.csv";

        public static string Dir =>
            Application.isEditor
                ? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Traces")
                : Path.Combine(Application.persistentDataPath, "traces");

        public static System.Collections.Generic.List<Core.Sim.Drivers.DriverTrace> LoadDataset(string requiredSplit = null)
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string key, string fallback)
            {
                int i = System.Array.IndexOf(args, key);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            }
            string split = Arg("-traceSplit", requiredSplit ?? "train");
            if (requiredSplit != null && split != requiredSplit)
                throw new System.ArgumentException("This operation requires split=" + requiredSplit);
            return Core.Sim.Drivers.TraceDataset.Load(Arg("-traceManifest", "datasets.tsv"), Dir, split);
        }

        public static void EnsureDir() => Directory.CreateDirectory(Dir);

        public static string PathFor(string label) => Path.Combine(Dir, label + Extension);

        public static string NewestFile()
        {
            if (!Directory.Exists(Dir)) return null;
            return Directory.GetFiles(Dir, "*" + Extension)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
    }
}
