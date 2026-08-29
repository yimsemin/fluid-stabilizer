using System.IO;
using FluidStabilizer.Core.Sim;
using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Locates <c>experiment.txt</c> — the shared operating-point file
    /// (<see cref="ExperimentProfile"/>). Repo root in the Editor (next to
    /// <c>Assets/</c>), alongside the executable in a build. First read writes a
    /// default file if none exists, so the owner always has something to edit.
    /// </summary>
    public static class ExperimentProfileStore
    {
        public const string FileName = "experiment.txt";

        public static string Path =>
            Application.isEditor
                ? System.IO.Path.Combine(Directory.GetParent(Application.dataPath).FullName, FileName)
                : System.IO.Path.Combine(
                    Directory.GetParent(Application.dataPath)?.FullName ?? Application.persistentDataPath, FileName);

        public static ExperimentProfile Load() => ExperimentProfile.LoadFromFileOrDefaults(Path);
    }
}
