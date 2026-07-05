using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Dom;

namespace Weva.Tests.Documents {
    public class ElementToBoxIndexTests {
        static UIDocumentState BuildState(string html) {
            var s = new UIDocumentBuilder {
                DocumentSource = html,
                StylesheetSources = new List<string>(),
                MediaContext = MediaContext.Default(800, 600)
            }.Build();
            UIDocumentLifecycle.RunLayout(s);
            return s;
        }

        [Test]
        public void Empty_index_returns_null_for_lookup() {
            var idx = new ElementToBoxIndex();
            Assert.That(idx.Lookup(new Element("p")), Is.Null);
            Assert.That(idx.Count, Is.EqualTo(0));
        }

        [Test]
        public void Rebuild_populates_map_with_layout_boxes() {
            var s = BuildState("<main><p>x</p><p>y</p></main>");
            Assert.That(s.ElementToBox.Count, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void Lookup_hit_returns_box_for_known_element() {
            var s = BuildState("<main id='m'><p>x</p></main>");
            var main = s.Doc.GetElementById("m");
            var box = s.ElementToBox.Lookup(main);
            Assert.That(box, Is.Not.Null);
            Assert.That(box.Element, Is.SameAs(main));
        }

        [Test]
        public void Lookup_miss_returns_null_for_unknown_element() {
            var s = BuildState("<main><p>x</p></main>");
            var orphan = new Element("section");
            Assert.That(s.ElementToBox.Lookup(orphan), Is.Null);
        }

        [Test]
        public void Rebuild_clears_old_entries() {
            var s = BuildState("<main><p>x</p></main>");
            int initialCount = s.ElementToBox.Count;
            s.ElementToBox.Rebuild(null);
            Assert.That(s.ElementToBox.Count, Is.EqualTo(0));
            // Re-running real layout repopulates.
            UIDocumentLifecycle.RunLayout(s);
            Assert.That(s.ElementToBox.Count, Is.EqualTo(initialCount));
        }
    }
}
