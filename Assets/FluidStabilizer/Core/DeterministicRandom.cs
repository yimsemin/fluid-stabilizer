using System;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core
{
    /// <summary>
    /// Default <see cref="IRandomSource"/>: a PCG32 stream plus named forking.
    /// Forks are derived by hashing the child name into the parent seed, so a new
    /// sub-stream can be added anywhere without shifting the numbers any existing
    /// sub-stream produces.
    /// </summary>
    public sealed class DeterministicRandom : IRandomSource
    {
        private readonly Pcg32 _pcg;
        private double _cachedGaussian;
        private bool _hasCachedGaussian;

        public DeterministicRandom(ulong seed)
        {
            Seed = seed;
            _pcg = new Pcg32(seed);
        }

        public ulong Seed { get; }

        public double NextDouble() => _pcg.NextDouble();

        public double NextDouble(double min, double max)
        {
            if (max < min) throw new ArgumentException("max < min");
            return min + (max - min) * _pcg.NextDouble();
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) throw new ArgumentException("maxExclusive <= minInclusive");
            uint range = (uint)((long)maxExclusive - minInclusive);
            // Unbiased bounded draw by rejection: discard the first (2^32 mod range) values.
            uint threshold = (0u - range) % range;
            uint x;
            do { x = _pcg.NextUInt(); } while (x < threshold);
            return minInclusive + (int)(x % range);
        }

        public double NextGaussian()
        {
            if (_hasCachedGaussian)
            {
                _hasCachedGaussian = false;
                return _cachedGaussian;
            }

            // Box–Muller.
            double u1 = 1.0 - _pcg.NextDouble(); // in (0, 1]
            double u2 = _pcg.NextDouble();
            double r = Math.Sqrt(-2.0 * Math.Log(u1));
            double theta = 2.0 * Math.PI * u2;
            _cachedGaussian = r * Math.Sin(theta);
            _hasCachedGaussian = true;
            return r * Math.Cos(theta);
        }

        public double NextGaussian(double mean, double stddev) => mean + stddev * NextGaussian();

        public IRandomSource Fork(string name)
        {
            ulong childSeed = Pcg32.SplitMix64(Seed ^ Pcg32.Fnv1a64(name ?? string.Empty));
            return new DeterministicRandom(childSeed);
        }
    }
}
