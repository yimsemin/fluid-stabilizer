using FluidStabilizer.Contracts;
using FluidStabilizer.Core;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// The "obvious" sensor-only baseline (design-review §2): low-pass the
    /// tank-IMU specific force and command the joint <em>angle</em> that would put
    /// the liquid surface perpendicular to it; the actuator's position servo
    /// tracks it. No integral, no slosh model — a pendulum-emulating controller.
    /// Its weakness is that the felt specific force is contaminated by the slosh
    /// reaction, so a low cutoff trades manoeuvre tracking against slosh rejection
    /// (design-review §2). Any learned policy must beat this convincingly.
    /// </summary>
    public sealed class SpecificForceAlignmentController : IController
    {
        private readonly double _cutoffHz;
        private readonly LowPass _fx, _fy, _fz;
        private long _seq;
        private double _lastStamp;

        public SpecificForceAlignmentController(double specificForceCutoffHz = 0.7)
        {
            _cutoffHz = specificForceCutoffHz;
            _fx = new LowPass(_cutoffHz);
            _fy = new LowPass(_cutoffHz);
            _fz = new LowPass(_cutoffHz);
        }

        public string Name => "SpecificForceAlignment";

        public void Reset()
        {
            _fx.Reset();
            _fy.Reset();
            _fz.Reset();
            _seq = 0;
            _lastStamp = 0.0;
        }

        public StabilizerCommand ComputeCommand(in SensorBundle s)
        {
            double dt = s.StampSeconds - _lastStamp;
            if (dt <= 0.0) dt = 0.01;
            _lastStamp = s.StampSeconds;

            var f = s.TankImu.SpecificForce;
            var fLp = new Double3(
                _fx.Step(f.X, dt),
                _fy.Step(f.Y, dt),
                _fz.Step(f.Z, dt));

            return StabilizerCommand.Angle(_seq++, s.StampSeconds,
                TiltGeometry.DesiredPitch(fLp), TiltGeometry.DesiredRoll(fLp));
        }
    }
}
