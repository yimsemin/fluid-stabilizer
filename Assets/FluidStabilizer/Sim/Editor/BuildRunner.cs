using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FluidStabilizer.SimEditor
{
    /// <summary>
    /// experiment-plan step F — one button that produces the standalone "world"
    /// app (project-goals.md: Unity = the plant, a finished compiled program).
    /// Rebuilds the play scene, then builds the player for the active target into
    /// <c>Builds/</c>. The learned policy still runs as its own process
    /// (<c>python/serve_policy.py</c>) and the app connects to it — see the
    /// architecture note in project-goals.md.
    ///
    ///   Menu ▸ FluidStabilizer ▸ Build Standalone App
    ///   Unity -batchmode -quit -projectPath . \
    ///     -executeMethod FluidStabilizer.SimEditor.BuildRunner.Build
    /// </summary>
    public static class BuildRunner
    {
        [MenuItem("FluidStabilizer/Build Standalone App")]
        public static void Build()
        {
            SceneBuilder.Build(); // ensure PlayScene.unity exists and is in EditorBuildSettings

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[build] no enabled scenes in EditorBuildSettings.");
                return;
            }

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            string product = string.IsNullOrEmpty(PlayerSettings.productName) ? "fluid-stabilizer" : PlayerSettings.productName;

            string dir = Path.Combine("Builds", $"{product}-{target}");
            Directory.CreateDirectory(dir);
            string location = Path.Combine(dir, product + Extension(target));

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = location,
                target = target,
                targetGroup = group,
                options = BuildOptions.None,
            };

            Debug.Log($"[build] {target} → {location}  ({scenes.Length} scene(s))");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;

            if (s.result == BuildResult.Succeeded)
                Debug.Log($"[build] OK — {s.totalSize / (1024 * 1024)} MB in {s.totalTime.TotalSeconds:0} s → {s.outputPath}\n" +
                          "Run it, then start python/serve_policy.py and pick the learned stabilizer to see ③.");
            else
                Debug.LogError($"[build] {s.result} — {s.totalErrors} error(s). See the log above.");
        }

        private static string Extension(BuildTarget t)
        {
            switch (t)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64: return ".exe";
                case BuildTarget.StandaloneOSX: return ".app";
                default: return ""; // Linux: a bare executable
            }
        }
    }
}
