using System;
using NUnit.Framework;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Tests
{
    public class Double3Tests
    {
        [Test]
        public void AddSubtractScale_Basic()
        {
            var a = new Double3(1, 2, 3);
            var b = new Double3(4, 5, 6);

            Assert.AreEqual(new Double3(5, 7, 9), a + b);
            Assert.AreEqual(new Double3(-3, -3, -3), a - b);
            Assert.AreEqual(new Double3(2, 4, 6), a * 2.0);
            Assert.AreEqual(new Double3(2, 4, 6), 2.0 * a);
        }

        [Test]
        public void DotAndCross_MatchKnownValues()
        {
            Assert.AreEqual(0.0, Double3.Dot(Double3.UnitX, Double3.UnitY), 1e-15);
            Assert.AreEqual(Double3.UnitZ, Double3.Cross(Double3.UnitX, Double3.UnitY));
            Assert.AreEqual(-Double3.UnitZ, Double3.Cross(Double3.UnitY, Double3.UnitX));
        }

        [Test]
        public void Normalized_UnitLength_AndZeroSafe()
        {
            var n = new Double3(3, 0, 4).Normalized;
            Assert.AreEqual(1.0, n.Magnitude, 1e-12);
            Assert.AreEqual(new Double3(0.6, 0.0, 0.8), n);

            Assert.AreEqual(Double3.Zero, Double3.Zero.Normalized);
        }

        [Test]
        public void WrapPi_KeepsAnglesInRange()
        {
            Assert.AreEqual(0.0, MathD.WrapPi(2.0 * Math.PI), 1e-12);
            Assert.AreEqual(Math.PI, MathD.WrapPi(Math.PI), 1e-12);
            Assert.Less(MathD.WrapPi(3.5 * Math.PI), Math.PI + 1e-9);
            Assert.Greater(MathD.WrapPi(-3.5 * Math.PI), -Math.PI - 1e-9);
        }

        [Test]
        public void MoveToward_StopsAtTarget()
        {
            Assert.AreEqual(1.0, MathD.MoveToward(0.0, 1.0, 5.0), 1e-15);
            Assert.AreEqual(0.5, MathD.MoveToward(0.0, 1.0, 0.5), 1e-15);
            Assert.AreEqual(-0.5, MathD.MoveToward(0.0, -1.0, 0.5), 1e-15);
        }
    }
}
