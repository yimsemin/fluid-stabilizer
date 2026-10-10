using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using FluidStabilizer.Core.Sim;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;
using FluidStabilizer.Controller;
using FluidStabilizer.Rl;

namespace FluidStabilizer.Tests
{
    /// <summary>
    /// experiment-plan step C1 — the BC dataset export must be deterministic,
    /// correctly shaped, and its actions clamped to [-1, 1].
    /// </summary>
    public class BcDatasetExporterTests
    {
        private static DriverTrace ShortTrace(ulong seed = 3, double seconds = 4.0)
        {
            var cfg = new SimulationConfig { EpisodeSeconds = seconds };
            cfg.Slosh.FillRatio = 0.62;
            var trace = new DriverTrace
            {
                Label = "bc-" + seed, Seed = seed, StepSeconds = cfg.PhysicsStepSeconds, FillRatio = 0.62,
            };
            var loop = new SimulationLoop(cfg, new HoldController(),
                new RecordingDriver(new RandomManeuverDriver(seed, 0.7), trace), new PendulumSloshModel());
            loop.RunEpisode(seed);
            return trace;
        }

        private static List<BcDatasetExporter.TrajRow> Traj(int n, double amp)
        {
            var rows = new List<BcDatasetExporter.TrajRow>(n);
            for (int i = 0; i < n; i++)
                rows.Add(new BcDatasetExporter.TrajRow
                {
                    T = i * 0.01,
                    PitchRate = amp * System.Math.Sin(i * 0.05),
                    RollRate = -amp * System.Math.Cos(i * 0.05),
                });
            return rows;
        }

        [Test]
        public void FromDrive_IsDeterministic_AndShaped()
        {
            var trace = ShortTrace();
            var traj = Traj(500, 1.0);

            var a = BcDatasetExporter.FromDrive(trace, traj, () => new PendulumSloshModel(), 6);
            var b = BcDatasetExporter.FromDrive(trace, traj, () => new PendulumSloshModel(), 6);

            Assert.AreEqual(a.Count, b.Count);
            Assert.Greater(a.Count, 100);
            Assert.AreEqual(ObservationEncoder.SingleFrameSize * 6, a[0].Obs.Length);

            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].ActPitch, b[i].ActPitch);
                Assert.AreEqual(a[i].ActRoll, b[i].ActRoll);
                CollectionAssert.AreEqual(a[i].Obs, b[i].Obs);
            }
        }

        [Test]
        public void Actions_AreClampedToUnitRange_EvenForAnAbsurdTrajectory()
        {
            var trace = ShortTrace();
            var traj = Traj(500, 50.0); // way past the max rate

            var samples = BcDatasetExporter.FromDrive(trace, traj, () => new PendulumSloshModel(), 4);
            foreach (var s in samples)
            {
                Assert.LessOrEqual(s.ActPitch, 1.0f);
                Assert.GreaterOrEqual(s.ActPitch, -1.0f);
                Assert.LessOrEqual(s.ActRoll, 1.0f);
                Assert.GreaterOrEqual(s.ActRoll, -1.0f);
            }
        }

        [Test]
        public void TrajectoryLoader_RoundTrips()
        {
            const string csv = @"# fluid-stabilizer-hindsight-trajectory/v1
# durationSeconds: 1.0
t,pitchRate,rollRate
0.0000,0.5,-0.25
0.0100,0.6,-0.2
0.0200,-0.1,0.3
";
            using (var r = new StringReader(csv))
            {
                var rows = BcDatasetExporter.LoadTrajectory(r);
                Assert.AreEqual(3, rows.Count);
                Assert.AreEqual(0.6, rows[1].PitchRate, 1e-9);
                Assert.AreEqual(0.3, rows[2].RollRate, 1e-9);
            }
        }

        [Test]
        public void Write_EmitsHeaderAndOneRowPerSample()
        {
            var trace = ShortTrace();
            var samples = BcDatasetExporter.FromDrive(trace, Traj(300, 0.5), () => new PendulumSloshModel(), 6);

            using (var w = new StringWriter())
            {
                BcDatasetExporter.Write(w, samples, samples[0].Obs.Length, 6, 3.5, new[] { trace.Label });
                string[] lines = w.ToString().Split('\n');
                int header = 0, data = 0;
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    if (line[0] == '#' || line.StartsWith("o0,")) header++;
                    else data++;
                }
                Assert.AreEqual(9, header);            // 8 "# ..." (incl. contract, clampedFraction) + 1 column header
                Assert.AreEqual(samples.Count, data);
            }
        }

        // findings F11: a do-nothing teacher is the rigid tank, so its label must be
        // "stay locked" — "release + zero rate" would teach an unlocked servo.
        [Test]
        public void ExplicitLock_IdleTeacherLabelsLocked_ActiveTeacherLabelsRelease()
        {
            var trace = ShortTrace();
            var idle = BcDatasetExporter.FromDrive(trace, Traj(300, 0.0), () => new PendulumSloshModel(), 6,
                baselineId: "hold", residualScaleRadPerSec: 3.5, lockCapable: true, lockMode: "explicit");
            var active = BcDatasetExporter.FromDrive(trace, Traj(300, 0.5), () => new PendulumSloshModel(), 6,
                baselineId: "hold", residualScaleRadPerSec: 3.5, lockCapable: true, lockMode: "explicit");
            Assert.IsNotEmpty(idle);
            foreach (var s in idle) Assert.AreEqual(0f, s.ActLock);
            foreach (var s in active) Assert.AreEqual(1f, s.ActLock);
        }
    }
}
