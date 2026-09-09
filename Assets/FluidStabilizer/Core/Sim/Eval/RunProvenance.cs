using System;
using System.IO;
using System.Linq;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim.Drivers;

namespace FluidStabilizer.Core.Sim.Eval
{
    public static class RunProvenance
    {
        // MVID identifies the binaries actually loaded, also in standalone builds without .git.
        public static string CodeIdentity => string.Join(";", AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name.StartsWith("FluidStabilizer", StringComparison.Ordinal))
            .OrderBy(a => a.GetName().Name)
            .Select(a => a.GetName().Name + "@" + a.ManifestModule.ModuleVersionId));

        public static string NewPath(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir ?? "", Path.GetFileNameWithoutExtension(path) + "-" +
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        }

        public static string Describe(DriverTrace trace, SimulationConfig config, string model, IController controller = null)
        {
            var policy = controller as IPolicyExecutionStatus;
            return "code=" + CodeIdentity + "\ntrace=" + trace.SourcePath + "\nsourceSha256=" + trace.SourceSha256
                + "\ninputSha256=" + trace.InputSha256 + "\nsplit=" + trace.DatasetSplit
                + "\nmanifestSha256=" + trace.DatasetManifestSha256 + "\nhistory=" + trace.DatasetHistory
                + "\nrecordedCode=" + trace.RecordedCode + "\nrecordedController=" + trace.RecordedController
                + "\nseed=" + trace.Seed + "\nslosh=" + model + "\nassumptions=" + trace.Assumptions
                + "\neffectiveConfig=" + ConfigSnapshot.Encode(config)
                + "\nrequestedController=" + controller?.GetType().FullName + "\nstatus=" + (policy?.ExecutionStatus ?? "analytic")
                + "\npolicy=" + policy?.PolicyIdentity + "\npolicySettings=" + policy?.PolicySettings
                + "\npolicyTicks=" + policy?.PolicyTicks + "\nfallbackTicks=" + policy?.FallbackTicks
                + "\nfallbackReason=" + policy?.FallbackReason;
        }
    }
}
