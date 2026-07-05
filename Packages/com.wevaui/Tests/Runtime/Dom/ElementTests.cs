using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Dom;

namespace Weva.Tests.Dom {
    public class ElementTests {
        [Test]
        public void TagName_is_stored_as_given() {
            var e = new Element("div");
            Assert.That(e.TagName, Is.EqualTo("div"));
        }

        [Test]
        public void GetAttribute_returns_null_when_missing() {
            var e = new Element("div");
            Assert.That(e.GetAttribute("id"), Is.Null);
        }

        [Test]
        public void SetAttribute_then_GetAttribute_round_trips() {
            var e = new Element("div");
            e.SetAttribute("id", "main");
            Assert.That(e.GetAttribute("id"), Is.EqualTo("main"));
        }

        [Test]
        public void HasAttribute_reflects_presence() {
            var e = new Element("div");
            Assert.That(e.HasAttribute("id"), Is.False);
            e.SetAttribute("id", "x");
            Assert.That(e.HasAttribute("id"), Is.True);
        }

        [Test]
        public void Id_returns_id_attribute() {
            var e = new Element("div");
            e.SetAttribute("id", "main");
            Assert.That(e.Id, Is.EqualTo("main"));
        }

        [Test]
        public void Id_returns_null_when_missing() {
            var e = new Element("div");
            Assert.That(e.Id, Is.Null);
        }

        [TestCase("foo bar baz", new[] { "foo", "bar", "baz" })]
        [TestCase("  foo   bar  ", new[] { "foo", "bar" })]
        [TestCase("solo", new[] { "solo" })]
        [TestCase("foo\tbar\nbaz", new[] { "foo", "bar", "baz" })]
        public void ClassList_splits_on_whitespace(string className, string[] expected) {
            var e = new Element("div");
            e.SetAttribute("class", className);
            Assert.That(e.ClassList.ToList(), Is.EqualTo(expected.ToList()));
        }

        [Test]
        public void ClassList_empty_when_no_class_attribute() {
            var e = new Element("div");
            Assert.That(e.ClassList.Any(), Is.False);
        }

        [Test]
        public void ClassList_empty_when_class_is_empty() {
            var e = new Element("div");
            e.SetAttribute("class", "");
            Assert.That(e.ClassList.Any(), Is.False);
        }
    }
}
