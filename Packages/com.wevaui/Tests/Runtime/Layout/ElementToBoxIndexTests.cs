using System.Linq;
using NUnit.Framework;
using Weva.Documents;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // A-BUTTON-BOXINDEX (CSS_OPEN_GAPS): `<button>` elements were
    // unresolvable through ElementToBoxIndex in the live editor while every
    // sibling tag resolved — the DevTools Styles panel self-unpinned on
    // buttons. These tests pin the index contract: every element-owning
    // ELEMENT box resolves, and the mapped box is the element's PRINCIPAL
    // box (never a TextRun, which shares the element pointer for styling).
    public class ElementToBoxIndexTests {

        static (Box root, Weva.Dom.Element el) BuildWith(string html, string css, string tag) {
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var box = AllBoxes(root).First(b =>
                b.Element != null && b.Element.TagName == tag && !(b is TextRun));
            return (root, box.Element);
        }

        [Test]
        public void Button_resolves_to_its_principal_box() {
            var (root, btn) = BuildWith(
                "<div><button id='b'>Click me</button></div>",
                "button { padding: 8px 16px; }", "button");
            var index = new ElementToBoxIndex();
            index.Rebuild(root);
            var box = index.Lookup(btn);
            Assert.That(box, Is.Not.Null, "button must be in the index");
            Assert.That(box, Is.Not.InstanceOf<TextRun>(),
                "the mapped box must be the principal box, not the text fragment");
            Assert.That(box.Element, Is.SameAs(btn));
        }

        [Test]
        public void Text_bearing_div_maps_to_principal_box_not_textrun() {
            // The general form of the hazard: the depth-first walk visits the
            // element's TextRun fragments AFTER its principal box, and
            // last-write-wins would leave the map pointing at a TextRun.
            var (root, el) = BuildWith(
                "<div id='d'>Hello world</div>", "div { padding: 4px; }", "div");
            var index = new ElementToBoxIndex();
            index.Rebuild(root);
            var box = index.Lookup(el);
            Assert.That(box, Is.Not.Null);
            Assert.That(box, Is.Not.InstanceOf<TextRun>(),
                $"principal box expected, got {box.GetType().Name}");
        }

        [Test]
        public void All_element_tags_resolve() {
            const string html =
                "<section><h2>Head</h2><p>Para text</p>" +
                "<button>Go</button><div><span>inline</span></div></section>";
            var (root, _, _) = Build(html, "button { padding: 4px; }", viewportWidth: 800, viewportHeight: 600);
            var index = new ElementToBoxIndex();
            index.Rebuild(root);
            foreach (var b in AllBoxes(root).Where(b => b.Element != null && !(b is TextRun))) {
                Assert.That(index.Lookup(b.Element), Is.Not.Null,
                    $"<{b.Element.TagName}> must resolve");
            }
        }
    }
}
