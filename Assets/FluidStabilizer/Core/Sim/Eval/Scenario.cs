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
                // The intended "interesting" regime: freeboard ~0.30 m, brisk
                // (but realistic) highway driving.
                Name = "HighwayBriskLowFreeboard",
                Config = () =>
                {
                    var c = new SimulationConfig { EpisodeSeconds = 40.0 };
                    c.Slosh.FillRatio = 0.66; // 0.53 m liquid ⇒ 0.27 m freeboard
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
                    c.Slosh.FillRatio = 0.72;     // 0.576 m liquid ⇒ 0.224 m freeboard
                    c.Slosh.DampingRatio = 0.04;
                    return c;
                },
                Driver = seed => new RandomManeuverDriver(seed, aggression: 0.7)
            },
        };
    }
}
