using FluidStabilizer.Contracts;
using FluidStabilizer.Core;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// Performance-ceiling baseline (design-review §2, §12).
    ///
    /// Diagnostic finding (`PlantControllabilityTests`): if the tank tilt tracks
    /// the quasi-static ideal tilt the wall wave shrinks ~12×; through a
    /// first-order lag it still shrinks ~3×. High-gain feedback on the *felt*
    /// tilt fails only because that signal oscillates at the slosh frequency and
    /// the loop resonates. So the ceiling is: command the joint <em>angle</em> to
    /// the low-passed slosh-free ideal tilt and let the servo track it — no
    /// slosh-state feedback (the liquid's own damping clears the residual).
    ///
    /// Privileged: it uses the slosh-free specific force. A sensor-only version
    /// must estimate it (that is <see cref="SpecificForceAlignmentController"/>).
    /// Never deployed.
    /// </summary>
    public sealed class PrivilegedFullStateController : IPrivilegedController
    {
        private readonly LowPass _tgtPitch, _tgtRoll;
        private long _seq;
        private double _lastStamp;

        public PrivilegedFullStateController(double targetCutoffHz = 2.0)
        {
            _tgtPitch = new LowPass(targetCutoffHz);
            _tgtRoll = new LowPass(targetCutoffHz);
        }

        public string Name => "PrivilegedFeedforwardTilt (ceiling)";

        public void Reset()
        {
            _tgtPitch.Reset();
            _tgtRoll.Reset();
            _seq = 0;
            _lastStamp = 0.0;
        }

        public StabilizerCommand ComputeCommand(in SensorBundle s)
            => StabilizerCommand.Hold(_seq++, s.StampSeconds);

        public StabilizerCommand ComputeCommand(in SensorBundle s, in PrivilegedObservation p)
        {
            double dt = p.StampSeconds - _lastStamp;
            if (dt <= 0.0) dt = 0.01;
            _lastStamp = p.StampSeconds;

            var f = p.SpecificForceTankFrame; // slosh-free
            double tgtPitch = _tgtPitch.Step(TiltGeometry.DesiredPitch(f), dt);
            double tgtRoll = _tgtRoll.Step(TiltGeometry.DesiredRoll(f), dt);

            return StabilizerCommand.Angle(_seq++, s.StampSeconds, tgtPitch, tgtRoll);
        }
    }
}
