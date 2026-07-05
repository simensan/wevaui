using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;

namespace Weva.Tests.Css.Cascade {
    public class ComputedStyleTests {
        [Test]
        public void Element_property_round_trips() {
            var e = new Element("div");
            var cs = new ComputedStyle(e);
            Assert.That(cs.Element, Is.SameAs(e));
        }

        [Test]
        public void Get_returns_null_when_unset() {
            var cs = new ComputedStyle(new Element("div"));
            Assert.That(cs.Get("color"), Is.Null);
        }

        [Test]
        public void TryGet_returns_false_when_unset() {
            var cs = new ComputedStyle(new Element("div"));
            Assert.That(cs.TryGet("color", out var v), Is.False);
            Assert.That(v, Is.Null);
        }

        [Test]
        public void Set_then_get_returns_value() {
            var cs = new ComputedStyle(new Element("div"));
            cs.Set("color", "red");
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Set_then_TryGet_returns_true_with_value() {
            var cs = new ComputedStyle(new Element("div"));
            cs.Set("font-size", "16px");
            Assert.That(cs.TryGet("font-size", out var v), Is.True);
            Assert.That(v, Is.EqualTo("16px"));
        }

        [Test]
        public void Set_overwrites_previous_value() {
            var cs = new ComputedStyle(new Element("div"));
            cs.Set("color", "red");
            cs.Set("color", "blue");
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Enumerate_returns_only_set_properties() {
            var cs = new ComputedStyle(new Element("div"));
            cs.Set("color", "red");
            cs.Set("width", "100px");
            var dict = cs.Enumerate().ToDictionary(p => p.Key, p => p.Value);
            Assert.That(dict, Has.Count.EqualTo(2));
            Assert.That(dict["color"], Is.EqualTo("red"));
            Assert.That(dict["width"], Is.EqualTo("100px"));
        }

        [Test]
        public void Enumerate_on_empty_returns_no_pairs() {
            var cs = new ComputedStyle(new Element("div"));
            Assert.That(cs.Enumerate().ToList(), Is.Empty);
        }

        [Test]
        public void Contains_reflects_set_state() {
            var cs = new ComputedStyle(new Element("div"));
            Assert.That(cs.Contains("display"), Is.False);
            cs.Set("display", "block");
            Assert.That(cs.Contains("display"), Is.True);
        }

        [Test]
        public void Custom_properties_round_trip() {
            var cs = new ComputedStyle(new Element("div"));
            cs.Set("--accent", "#ff8800");
            Assert.That(cs.Get("--accent"), Is.EqualTo("#ff8800"));
            Assert.That(cs.TryGet("--accent", out var v), Is.True);
            Assert.That(v, Is.EqualTo("#ff8800"));
        }

        [Test]
        public void Get_returns_null_for_null_property() {
            var cs = new ComputedStyle(new Element("div"));
            Assert.That(cs.Get(null), Is.Null);
        }
    }
}
