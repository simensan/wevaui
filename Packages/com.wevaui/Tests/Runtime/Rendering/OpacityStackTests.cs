using System;
using NUnit.Framework;
using Weva.Rendering;

namespace Weva.Tests.Rendering {
    public class OpacityStackTests {
        const float Eps = 1e-5f;

        [Test]
        public void Initial_top_is_one() {
            var s = new OpacityStack();
            Assert.That(s.Current, Is.EqualTo(1f));
            Assert.That(s.Depth, Is.EqualTo(0));
        }

        [Test]
        public void Push_multiplies_with_current_top() {
            var s = new OpacityStack();
            s.Push(0.5f);
            Assert.That(s.Current, Is.EqualTo(0.5f).Within(Eps));
            s.Push(0.5f);
            Assert.That(s.Current, Is.EqualTo(0.25f).Within(Eps));
        }

        [Test]
        public void Pop_restores_previous_top() {
            var s = new OpacityStack();
            s.Push(0.5f);
            s.Push(0.5f);
            s.Pop();
            Assert.That(s.Current, Is.EqualTo(0.5f).Within(Eps));
            s.Pop();
            Assert.That(s.Current, Is.EqualTo(1f).Within(Eps));
        }

        [Test]
        public void Pop_underflow_throws() {
            var s = new OpacityStack();
            Assert.Throws<InvalidOperationException>(() => s.Pop());
        }

        [Test]
        public void Push_clamps_negative_to_zero() {
            var s = new OpacityStack();
            s.Push(-0.5f);
            Assert.That(s.Current, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void Push_clamps_above_one_to_one() {
            var s = new OpacityStack();
            s.Push(2f);
            Assert.That(s.Current, Is.EqualTo(1f).Within(Eps));
        }
    }
}
