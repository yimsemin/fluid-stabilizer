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
