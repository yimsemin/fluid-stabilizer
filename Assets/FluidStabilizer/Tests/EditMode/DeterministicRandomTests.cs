using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;
using FluidStabilizer.Core;

namespace FluidStabilizer.Tests
{
    public class DeterministicRandomTests
    {
        [Test]
        public void SameSeed_ProducesIdenticalSequence()
        {
            var a = new DeterministicRandom(12345);
            var b = new DeterministicRandom(12345);

            for (int i = 0; i < 1000; i++)
                Assert.AreEqual(a.NextDouble(), b.NextDouble());
        }

        [Test]
        public void DifferentSeed_Diverges()
        {
            var a = new DeterministicRandom(1);
            var b = new DeterministicRandom(2);

            bool anyDifferent = false;
            for (int i = 0; i < 10; i++)
                anyDifferent |= a.NextDouble() != b.NextDouble();

            Assert.IsTrue(anyDifferent);
        }

        [Test]
        public void Fork_IsStableByName_AndIndependentOfSiblingOrder()
        {
            var rootA = new DeterministicRandom(999);
            var noiseA = rootA.Fork("sensor-noise");
            var domainA = rootA.Fork("domain-randomization");

            // Same names, opposite fork order, fresh root with the same seed.
            var rootB = new DeterministicRandom(999);
            var domainB = rootB.Fork("domain-randomization");
            var noiseB = rootB.Fork("sensor-noise");

            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(noiseA.NextDouble(), noiseB.NextDouble(), "named fork not stable");
                Assert.AreEqual(domainA.NextDouble(), domainB.NextDouble(), "named fork not stable");
            }
        }

        [Test]
        public void Fork_DifferentNames_AreDecorrelated()
        {
            var root = new DeterministicRandom(7);
            var x = root.Fork("a");
            var y = root.Fork("b");

            bool anyDifferent = false;
            for (int i = 0; i < 10; i++)
                anyDifferent |= x.NextDouble() != y.NextDouble();

            Assert.IsTrue(anyDifferent);
        }

        [Test]
        public void NextDouble_StaysInRange()
        {
            var r = new DeterministicRandom(42);
            for (int i = 0; i < 100000; i++)
            {
                double v = r.NextDouble();
                Assert.GreaterOrEqual(v, 0.0);
                Assert.Less(v, 1.0);
            }
        }

        [Test]
        public void NextInt_CoversRange_AndStaysInBounds()
        {
            var r = new DeterministicRandom(42);
            var seen = new bool[5];
            for (int i = 0; i < 100000; i++)
            {
                int v = r.NextInt(0, 5);
                Assert.GreaterOrEqual(v, 0);
                Assert.Less(v, 5);
                seen[v] = true;
            }
            Assert.IsTrue(Array.TrueForAll(seen, s => s), "not all values in [0,5) produced");
        }

        [Test]
        public void NextGaussian_HasApproxZeroMeanUnitVariance()
        {
            var r = new DeterministicRandom(2024);
            const int n = 200000;
            double sum = 0, sumSq = 0;
            for (int i = 0; i < n; i++)
            {
                double v = r.NextGaussian();
                sum += v;
                sumSq += v * v;
            }
            double mean = sum / n;
            double variance = sumSq / n - mean * mean;

            Assert.AreEqual(0.0, mean, 0.02);
            Assert.AreEqual(1.0, variance, 0.03);
        }

        [Test]
        public void FullEpisodeReplay_IsBitIdentical()
        {
            // Simulates the pattern the sim uses: a root seed forked into named
            // sub-streams that are drawn from interleaved across many steps.
            static double RunEpisode(ulong seed)
            {
                var root = new DeterministicRandom(seed);
                var noise = root.Fork("sensor-noise");
                var driver = root.Fork("driver");
                var slosh = root.Fork("slosh-init");
                double acc = 0;
                for (int step = 0; step < 10000; step++)
                {
                    acc += noise.NextGaussian() * 0.01;
                    acc += driver.NextDouble();
                    if (step % 50 == 0) acc += slosh.NextGaussian();
                }
                return acc;
            }

            Assert.AreEqual(RunEpisode(555), RunEpisode(555));
            Assert.AreNotEqual(RunEpisode(555), RunEpisode(556));
        }
    }
}
