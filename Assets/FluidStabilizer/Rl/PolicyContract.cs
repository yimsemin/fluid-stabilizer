using System;
using System.Collections.Generic;
using System.Globalization;
using FluidStabilizer.Contracts;
using FluidStabilizer.Controller;
using FluidStabilizer.Core.Sim;

namespace FluidStabilizer.Rl
{
    /// <summary>
    /// Everything needed to turn a policy's raw output into a stabilizer command,
    /// in one place, so training (<see cref="RlEnvironment"/>) and deployment
    /// (<see cref="RemotePolicyController"/>) cannot drift apart.
    ///
    /// Before this existed they had: training <c>rate = baseline + policy·0.35·maxRate</c>
    /// then a lock gate; deployment <c>rate = policy·maxRate</c> and no gate. A
    /// residual policy served through the bridge was executed with entirely
    /// different action semantics than it was trained with.
    ///
    /// A checkpoint carries its contract (written by the trainer, sent back in the
    /// v2 handshake). A policy whose contract cannot be established runs on the
    /// legacy plain path and is reported <c>policy-unverified</c> — never silently
    /// as a normal AI result.
    /// </summary>
    public struct PolicyContract : IEquatable<PolicyContract>
    {
        /// <summary>Bump when the observation layout changes; a mismatch is refused.</summary>
        public const int CurrentObsVersion = 2;

        /// <summary>Baseline the policy corrects. "none" = the policy acts alone.</summary>
        public string Baseline;

        /// <summary>What a policy output of ±1 maps to, rad/s.</summary>
        public double ActionScaleRadPerSec;

        /// <summary>Hard clamp on the composed rate, rad/s (the actuator's own limit).</summary>
        public double MaxRateRadPerSec;

        /// <summary>D8 hybrid action: mechanically lock instead of holding zero.</summary>
        public bool LockCapable;

        /// <summary>
        /// How the lock is decided. <c>"deadband"</c> = the original D8 gate infers
        /// it from the composed rate — which makes the action space a cliff with no
        /// gradient (findings F9). <c>"explicit"</c> = the policy emits a third
        /// action requesting the lock, so staying locked is a choice it can learn.
        /// Ignored when <see cref="LockCapable"/> is false.
        /// </summary>
        public string LockMode;

        public bool IsExplicitLock => LockCapable
            && string.Equals(LockMode, "explicit", StringComparison.OrdinalIgnoreCase);

        /// <summary>Policy outputs: 2 rate residuals, plus the lock request when explicit.</summary>
        public int ActionSize => IsExplicitLock ? 3 : 2;

        public int FrameStack;
        public int ObsVersion;

        public bool IsResidual => !string.IsNullOrEmpty(Baseline)
                                  && !Baseline.Equals("none", StringComparison.OrdinalIgnoreCase);

        /// <summary>The plain absolute-joint-rate contract every pre-2026-09 checkpoint was served with.</summary>
        public static PolicyContract Legacy(double maxRate, int frameStack) => new PolicyContract
        {
            Baseline = "none",
            ActionScaleRadPerSec = maxRate,
            MaxRateRadPerSec = maxRate,
            LockCapable = false,
            LockMode = "none",
            FrameStack = frameStack,
            ObsVersion = CurrentObsVersion,
        };

        public static PolicyContract Residual(GimbalConfig gimbal, string baseline,
            double actionScale = double.NaN, bool lockCapable = true, int frameStack = 6,
            string lockMode = "deadband")
            => new PolicyContract
            {
                Baseline = baseline,
                ActionScaleRadPerSec = double.IsNaN(actionScale)
                    ? 0.35 * gimbal.MaxRateRadPerSec : actionScale,
                MaxRateRadPerSec = gimbal.MaxRateRadPerSec,
                LockCapable = lockCapable,
                LockMode = lockCapable ? lockMode : "none",
                FrameStack = Math.Max(1, frameStack),
                ObsVersion = CurrentObsVersion,
            };

        /// <summary>Baselines a served policy may name. Kept small on purpose — an
        /// unknown name is refused rather than quietly resolved to "none".</summary>
        public static IController MakeBaseline(string name)
        {
            switch ((name ?? "none").Trim().ToLowerInvariant())
            {
                case "":
                case "none": return null;
                case "hold": return new HoldController();
                case "sfa":
                case "specificforcealignment": return new SpecificForceAlignmentController();
                case "pid": return new PidController();
                default: throw new ArgumentException("unknown baseline in policy contract: " + name);
            }
        }

        public string Encode()
        {
            var ic = CultureInfo.InvariantCulture;
            return string.Join(";", new[]
            {
                "baseline=" + (Baseline ?? "none"),
                "actionScale=" + ActionScaleRadPerSec.ToString("R", ic),
                "maxRate=" + MaxRateRadPerSec.ToString("R", ic),
                "lockCapable=" + (LockCapable ? "1" : "0"),
                "lockMode=" + (LockCapable ? (LockMode ?? "deadband") : "none"),
                "frameStack=" + FrameStack.ToString(ic),
                "obsVersion=" + ObsVersion.ToString(ic),
            });
        }

        public static bool TryParse(string s, out PolicyContract c)
        {
            c = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var ic = CultureInfo.InvariantCulture;
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in s.Split(';'))
            {
                int i = part.IndexOf('=');
                if (i > 0) kv[part.Substring(0, i).Trim()] = part.Substring(i + 1).Trim();
            }

            if (!kv.TryGetValue("actionScale", out string a) ||
                !double.TryParse(a, NumberStyles.Float, ic, out double actionScale)) return false;
            if (!kv.TryGetValue("maxRate", out string m) ||
                !double.TryParse(m, NumberStyles.Float, ic, out double maxRate)) return false;
            kv.TryGetValue("baseline", out string baseline);
            kv.TryGetValue("lockCapable", out string lockCapable);
            kv.TryGetValue("lockMode", out string lockMode);
            int.TryParse(kv.TryGetValue("frameStack", out string fs) ? fs : "6", NumberStyles.Integer, ic, out int frameStack);
            int.TryParse(kv.TryGetValue("obsVersion", out string ov) ? ov : "1", NumberStyles.Integer, ic, out int obsVersion);

            c = new PolicyContract
            {
                Baseline = string.IsNullOrEmpty(baseline) ? "none" : baseline,
                ActionScaleRadPerSec = actionScale,
                MaxRateRadPerSec = maxRate,
                LockCapable = lockCapable == "1" || string.Equals(lockCapable, "true", StringComparison.OrdinalIgnoreCase),
                LockMode = string.IsNullOrEmpty(lockMode) ? "deadband" : lockMode,
                FrameStack = Math.Max(1, frameStack),
                ObsVersion = obsVersion,
            };
            return true;
        }

        public bool Equals(PolicyContract o) =>
            string.Equals(Baseline ?? "none", o.Baseline ?? "none", StringComparison.OrdinalIgnoreCase)
            && ActionScaleRadPerSec.Equals(o.ActionScaleRadPerSec)
            && MaxRateRadPerSec.Equals(o.MaxRateRadPerSec)
            && LockCapable == o.LockCapable
            && string.Equals(LockMode ?? "deadband", o.LockMode ?? "deadband", StringComparison.OrdinalIgnoreCase)
            && FrameStack == o.FrameStack
            && ObsVersion == o.ObsVersion;

        public override bool Equals(object o) => o is PolicyContract p && Equals(p);
        public override int GetHashCode() => Encode().GetHashCode();
        public override string ToString() => Encode();
    }
}
