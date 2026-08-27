using FluidStabilizer.Contracts;
using FluidStabilizer.Core;

namespace FluidStabilizer.Controller
{
    /// <summary>
    /// The "obvious" baseline (design-review §2): low-pass the tank-IMU specific
    /// force, compute the pitch/roll that would put the liquid surface
    /// perpendicular to it, and drive the joints there with a proportional rate
    /// command. No integral, no slosh model — a well-tuned pendulum-emulating
    /// controller. Any learned policy must beat this convincingly to be worth it.
    /// </summary>
    public sealed class SpecificForceAlignmentController : IController
    {
        private readonly double _kp;
        private readonly double _cutoffHz;
        private readonly LowPass _fx, _fy, _fz;
        private long _seq;
        private double _lastStamp;

        public SpecificForceAlignmentController(double proportionalGain = 4.0, double specificForceCutoffHz = 1.2)
        {
            _kp = proportionalGain;
            _cutoffHz = specificForceCutoffHz;
            _fx = new LowPass(_cutoffHz);
            _fy = new LowPass(_cutoffHz);
            _fz = new LowPass(_cutoffHz);
        }

        public string Name => "SpecificForceAlignment (P)";

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

            double desiredPitch = TiltGeometry.DesiredPitch(fLp);
            double desiredRoll = TiltGeometry.DesiredRoll(fLp);

            return new StabilizerCommand
            {
                Sequence = _seq++,
                FromStampSeconds = s.StampSeconds,
                PitchRateCommand = _kp * (desiredPitch - s.Joints.PitchAngle),
                RollRateCommand = _kp * (desiredRoll - s.Joints.RollAngle)
            };
        }
    }
}
