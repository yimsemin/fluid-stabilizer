namespace FluidStabilizer.Core
{
    /// <summary>
    /// PCG-XSH-RR 64/32 pseudo-random generator (O'Neill, 2014). Small, fast,
    /// statistically solid, and fully deterministic across platforms — the sim
    /// requires bit-reproducible episodes from a seed, which rules out
    /// <c>UnityEngine.Random</c>.
    /// </summary>
    public sealed class Pcg32
    {
        private const ulong Multiplier = 6364136223846793005UL;

        private ulong _state;
        private readonly ulong _increment;

        public Pcg32(ulong seed, ulong sequence = 0xda3e39cb94b95bdbUL)
        {
            _increment = (sequence << 1) | 1UL;
            _state = 0UL;
            NextUInt();
            _state += seed;
            NextUInt();
        }

        /// <summary>Next 32-bit value, uniform over the full range.</summary>
        public uint NextUInt()
        {
            ulong old = _state;
            _state = old * Multiplier + _increment;
            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
        }

        /// <summary>Next 53-bit-mantissa double, uniform in [0, 1).</summary>
        public double NextDouble()
        {
            ulong hi = NextUInt();
            ulong lo = NextUInt();
            ulong x = (hi << 21) ^ lo; // 53 bits
            return (x & ((1UL << 53) - 1)) * (1.0 / (1UL << 53));
        }

        /// <summary>SplitMix64 — used to derive independent child seeds by name.</summary>
        public static ulong SplitMix64(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>FNV-1a 64-bit hash of a string (ordinal), for stable named forks.</summary>
        public static ulong Fnv1a64(string s)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= s[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }
}
