using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Reactive {
    // Audit LY11: the `value` attribute is mutated on EVERY keystroke in a
    // text control, but the tracker's generic attribute branch marked
    // Style|Layout|Paint — forcing a relayout per keystroke (FULL layout
    // when the input has inline siblings, via the ContainsInlines splice
    // bail). The value text is painted by the input overlay, not laid out
    // from the box tree, so Layout is wrong by default; the cascade's
    // narrow layout-diff (ApplyLayoutInvalidation) covers the rare
    // `[value=…] { width: … }` case.
    public class ValueAttributeInvalidationTests {
        static (Document doc, Element input, InvalidationTracker tracker) Setup() {
            var doc = HtmlParser.Parse("<div><input value=\"a\"><span>sibling</span></div>");
            var input = doc.GetElementsByTagName("input").First();
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            return (doc, input, tracker);
        }

        [Test]
        public void Value_attribute_change_is_not_layout_dirty_LY11() {
            var (_, input, tracker) = Setup();
            input.SetAttribute("value", "ab"); // a keystroke
            var kinds = tracker.GetKinds(input);
            Assert.That((kinds & InvalidationKind.Paint) != 0, Is.True, "the overlay repaints");
            Assert.That((kinds & InvalidationKind.Style) != 0, Is.True,
                "[value=…] / :placeholder-shown rules must re-match");
            Assert.That((kinds & InvalidationKind.Layout), Is.EqualTo(InvalidationKind.None),
                "typing must not force a relayout per keystroke (audit LY11)");
        }

        [Test]
        public void Placeholder_attribute_change_is_not_layout_dirty_LY11() {
            var (_, input, tracker) = Setup();
            input.SetAttribute("placeholder", "type here");
            Assert.That((tracker.GetKinds(input) & InvalidationKind.Layout), Is.EqualTo(InvalidationKind.None));
        }

        [Test]
        public void Other_attribute_changes_keep_the_layout_mark() {
            // The narrowing is value/placeholder-specific: generic attributes
            // (e.g. type, style hooks) keep the conservative Layout mark.
            var (_, input, tracker) = Setup();
            input.SetAttribute("data-state", "open");
            Assert.That((tracker.GetKinds(input) & InvalidationKind.Layout) != 0, Is.True);
        }
    }
}
