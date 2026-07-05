using System;
using NUnit.Framework;
using Weva.Paint;
using Weva.Rendering;

namespace Weva.Tests.Rendering {
    public class TransformStackTests {
        const double Eps = 1e-5;

        [Test]
        public void Initial_top_is_identity() {
            var s = new TransformStack();
            Assert.That(s.Current, Is.EqualTo(Transform2D.Identity));
            Assert.That(s.Depth, Is.EqualTo(0));
        }

        [Test]
        public void Push_combines_with_current_top() {
            var s = new TransformStack();
            s.Push(Transform2D.Translate(5, 7));
            var (x, y) = s.Current.Apply(0, 0);
            Assert.That(x, Is.EqualTo(5).Within(Eps));
            Assert.That(y, Is.EqualTo(7).Within(Eps));
            Assert.That(s.Depth, Is.EqualTo(1));
        }

        [Test]
        public void Push_then_pop_restores_previous_top() {
            var s = new TransformStack();
            var t = Transform2D.Translate(10, 0);
            s.Push(t);
            s.Pop();
            Assert.That(s.Current, Is.EqualTo(Transform2D.Identity));
            Assert.That(s.Depth, Is.EqualTo(0));
        }

        [Test]
        public void Nested_pushes_compose_correctly() {
            var s = new TransformStack();
            s.Push(Transform2D.Translate(10, 0));
            s.Push(Transform2D.Translate(0, 5));
            var (x, y) = s.Current.Apply(1, 1);
            // Inner translate (0, 5) applies first → (1, 6); then outer translate (10, 0)
            // applies → (11, 6).
            Assert.That(x, Is.EqualTo(11).Within(Eps));
            Assert.That(y, Is.EqualTo(6).Within(Eps));
        }

        [Test]
        public void Pop_underflow_throws() {
            var s = new TransformStack();
            Assert.Throws<InvalidOperationException>(() => s.Pop());
        }

        [Test]
        public void Reset_returns_to_identity() {
            var s = new TransformStack();
            s.Push(Transform2D.Translate(50, 50));
            s.Push(Transform2D.Scale(2, 2));
            s.Reset();
            Assert.That(s.Current, Is.EqualTo(Transform2D.Identity));
            Assert.That(s.Depth, Is.EqualTo(0));
        }

        [Test]
        public void Push_scale_then_translate_orders_inside_out() {
            var s = new TransformStack();
            s.Push(Transform2D.Scale(2, 2));
            s.Push(Transform2D.Translate(10, 0));
            // Inner translate first: (1,1) -> (11, 1); then outer scale: (22, 2).
            var (x, y) = s.Current.Apply(1, 1);
            Assert.That(x, Is.EqualTo(22).Within(Eps));
            Assert.That(y, Is.EqualTo(2).Within(Eps));
        }
    }
}
