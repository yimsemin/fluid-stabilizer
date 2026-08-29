using System;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core.Sim.Drivers;
using FluidStabilizer.Core.Sim.SloshModels;

namespace FluidStabilizer.Core.Sim.Eval
{
    /// <summary>One evaluation condition: a config, a per-seed driver, a slosh model.</summary>
    public sealed class Scenario
    {
        public string Name;
        public Func<SimulationConfig> Config;
        public Func<ulong, IDriverInput> Driver;
        public Func<ISloshModel> Slosh = () => new PendulumSloshModel();
    }

    /// <summary>The fixed v1 evaluation scenario set (design-review §10, §17).</summary>
    public static class ScenarioSet
    {
        public static Scenario[] Default() => new[]
        {
            new Scenario
            {
                Name = "HighwayCalm",
                Config = () => new SimulationConfig { EpisodeSeconds = 30.0 },
                Driver = seed => new HighwayDriver(seed, briskness: 0.2)
            },
            new Scenario
            {
                Name = "HighwayBrisk",
                Config = () => new SimulationConfig { EpisodeSeconds = 30.0 },
                Driver = seed => new HighwayDriver(seed, briskness: 0.6)
            },
            new Scenario
            {
                // The intended "interesting" regime: tight freeboard, brisk
                // (but realistic) highway driving.
                Name = "HighwayBriskLowFreeboard",
                Config = () =>
                {
                    var c = new SimulationConfig { EpisodeSeconds = 40.0 };
                    c.Slosh.FillRatio = 0.66; // 0.40 m liquid ⇒ 0.20 m freeboard
                    return c;
                },
                Driver = seed => new HighwayDriver(seed, briskness: 0.9)
            },
            new Scenario
            {
                Name = "ScriptedManeuver",
                Config = () => new SimulationConfig { EpisodeSeconds = 22.0 },
                Driver = _ => new ScriptedManeuverDriver()
            },
            new Scenario
            {
                Name = "AggressiveRandom",
                Config = () => new SimulationConfig { EpisodeSeconds = 30.0 },
                Driver = seed => new RandomManeuverDriver(seed, aggression: 0.7)
            },
            new Scenario
            {
                Name = "HighFillAggressive",
                Config = () =>
                {
                    var c = new SimulationConfig { EpisodeSeconds = 30.0 };
                    c.Slosh.FillRatio = 0.72;     // 0.43 m liquid ⇒ 0.17 m freeboard
                    c.Slosh.DampingRatio = 0.04;
                    return c;
                },
                Driver = seed => new RandomManeuverDriver(seed, aggression: 0.7)
            },
            new Scenario
            {
                // Gentle highway driving, but a real road: rolling grade, curve
                // bank, surface roughness and the occasional pothole/joint. A
                // rigidly-held tank transmits every bump to the liquid — the
                // disturbance a learned/anticipatory policy is meant to reject.
                Name = "RoughRoadHighway",
                Config = () =>
                {
                    var c = new SimulationConfig { EpisodeSeconds = 40.0 };
                    c.Slosh.FillRatio = 0.60;    // 0.36 m liquid ⇒ 0.24 m freeboard
                    c.Road.Enabled = true;
                    return c;
                },
                Driver = seed => new HighwayDriver(seed, briskness: 0.45)
            },
            new Scenario
            {
                Name = "RoughRoadLowFreeboard",
                Config = () =>
                {
                    var c = new SimulationConfig { EpisodeSeconds = 40.0 };
                    c.Slosh.FillRatio = 0.66;    // 0.40 m liquid ⇒ 0.20 m freeboard
                    c.Road.Enabled = true;
                    c.Road.Severity = 1.5;      // coarse / broken surface
                    return c;
                },
                Driver = seed => new HighwayDriver(seed, briskness: 0.6)
            },
        };
    }
}
