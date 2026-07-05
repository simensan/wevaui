using System.Linq;
using NUnit.Framework;
using Weva.Dom;

namespace Weva.Tests.Dom {
    public class DocumentTests {
        static Document Build() {
            var doc = new Document();
            var root = new Element("div");
            root.SetAttribute("id", "root");
            root.SetAttribute("class", "container");

            var a = new Element("p");
            a.SetAttribute("id", "a");
            a.SetAttribute("class", "item highlighted");

            var b = new Element("p");
            b.SetAttribute("id", "b");
            b.SetAttribute("class", "item");

            var span = new Element("span");
            span.SetAttribute("id", "deep");

            a.AppendChild(span);
            root.AppendChild(a);
            root.AppendChild(b);
            doc.AppendChild(root);
            return doc;
        }

        [Test]
        public void GetElementById_finds_top_level() {
            var doc = Build();
            var e = doc.GetElementById("root");
            Assert.That(e, Is.Not.Null);
            Assert.That(e.TagName, Is.EqualTo("div"));
        }

        [Test]
        public void GetElementById_finds_nested() {
            var doc = Build();
            var e = doc.GetElementById("deep");
            Assert.That(e, Is.Not.Null);
            Assert.That(e.TagName, Is.EqualTo("span"));
        }

        [Test]
        public void GetElementById_returns_null_when_missing() {
            var doc = Build();
            Assert.That(doc.GetElementById("nope"), Is.Null);
        }

        [Test]
        public void GetElementsByTagName_returns_all_matches_in_document_order() {
            var doc = Build();
            var ps = doc.GetElementsByTagName("p").ToList();
            Assert.That(ps, Has.Count.EqualTo(2));
            Assert.That(ps[0].GetAttribute("id"), Is.EqualTo("a"));
            Assert.That(ps[1].GetAttribute("id"), Is.EqualTo("b"));
        }

        [Test]
        public void GetElementsByTagName_is_case_insensitive() {
            var doc = Build();
            var ps = doc.GetElementsByTagName("P").ToList();
            Assert.That(ps, Has.Count.EqualTo(2));
        }

        [Test]
        public void GetElementsByTagName_returns_empty_when_none_match() {
            var doc = Build();
            Assert.That(doc.GetElementsByTagName("button").Any(), Is.False);
        }

        [Test]
        public void GetElementsByClassName_finds_single() {
            var doc = Build();
            var matches = doc.GetElementsByClassName("highlighted").ToList();
            Assert.That(matches, Has.Count.EqualTo(1));
            Assert.That(matches[0].GetAttribute("id"), Is.EqualTo("a"));
        }

        [Test]
        public void GetElementsByClassName_finds_all_with_class_token() {
            var doc = Build();
            var matches = doc.GetElementsByClassName("item").ToList();
            Assert.That(matches, Has.Count.EqualTo(2));
        }
    }
}
