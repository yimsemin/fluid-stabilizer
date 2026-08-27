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
                Name = "CalmCruise",
                Config = () => new SimulationConfig { EpisodeSeconds = 25.0 },
                Driver = _ => new ConstantDriver(new DriverControls { Throttle = 0.13 })
            },
            new Scenario
            {
                Name = "ScriptedManeuver",
                Config = () => new SimulationConfig { EpisodeSeconds = 22.0 },
                Driver = _ => new ScriptedManeuverDriver()
            },
            new Scenario
            {
                Name = "RandomModerate",
                Config = () => new SimulationConfig { EpisodeSeconds = 30.0 },
                Driver = seed => new RandomManeuverDriver(seed, aggression: 0.4)
            },
            new Scenario
            {
                // The intended "interesting" regime: freeboard ~0.30 m, brisk
                // driving. Hold spills; a good controller should not.
                Name = "ModerateFreeboard",
                Config = () =>
                {
                    var c = new SimulationConfig { EpisodeSeconds = 30.0 };
                    c.Slosh.FillRatio = 0.625; // 0.50 m liquid ⇒ 0.30 m freeboard
                    return c;
                },
                Driver = seed => new RandomManeuverDriver(seed, aggression: 0.6)
            },
            new Scenario
            {
                Name = "RandomAggressive",
                Config = () => new SimulationConfig { EpisodeSeconds = 30.0 },
                Driver = seed => new RandomManeuverDriver(seed, aggression: 0.9)
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
