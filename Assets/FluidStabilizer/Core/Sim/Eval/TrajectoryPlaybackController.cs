using System;
using System.Collections.Generic;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim.Eval
{
    /// <summary>
    /// Replays an already-optimised gimbal-rate trajectory sampled at control rate
    /// (the rows <c>HindsightOptimizer.WriteTrajectoryCsv</c> writes), so the
    /// feasibility ceiling ④ can be watched as a lane in the play scene rather
    /// than only read in a table.
    ///
    /// This is <b>not</b> a controller in the honest sense: it ignores its sensors
    /// and plays back a solution computed offline with full knowledge of the whole
    /// drive. It is only meaningful on the exact drive it was optimised for.
    /// </summary>
    public sealed class TrajectoryPlaybackController : IController
    {
        private readonly IReadOnlyList<(double T, double PitchRate, double RollRate)> _rows;
        private long _seq;
        private int _cursor;

        public TrajectoryPlaybackController(IReadOnlyList<(double, double, double)> rows)
        {
            if (rows == null || rows.Count == 0)
                throw new ArgumentException("trajectory has no rows", nameof(rows));
            var copy = new List<(double, double, double)>(rows.Count);
            foreach (var r in rows) copy.Add(r);
            _rows = copy.ConvertAll(r => (r.Item1, r.Item2, r.Item3));
        }

        public string Name => "④ Hindsight ceiling (offline optimum, replayed)";

        public void Reset() { _seq = 0; _cursor = 0; }

        public StabilizerCommand ComputeCommand(in SensorBundle sensors)
        {
            double t = sensors.StampSeconds;
            // Rows are in ascending time and consumed in order, so a forward scan
            // stays O(1) amortised across the episode.
            while (_cursor + 1 < _rows.Count && _rows[_cursor + 1].T <= t) _cursor++;
            var row = _rows[_cursor];
            return StabilizerCommand.Rate(_seq++, t, row.PitchRate, row.RollRate);
        }
    }
}
