using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // BoxBuilder injects a synthetic `::backdrop` BlockBox immediately before
    // each top-layer host element when the cascade-supplied backdrop resolver
    // is wired up. Exercise both the managed BoxBuilder path (BuildDocument)
    // and the fallback (no resolver) path. Tests verify host detection by
    // attribute, that closing the modal/popover removes the backdrop, and
    // that the backdrop's style carries the expected cascaded values.
    public class BackdropBoxBuilderTests {
        static (Box root, CascadeEngine engine) BuildWithBackdrop(string html, string css = null) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UA(BuiltinUserAgent));
            if (!string.IsNullOrEmpty(css)) sheets.Add(Author(css));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var bb = new BoxBuilder(
                e => styles.TryGetValue(e, out var cs) ? cs : null,
                e => engine.ComputeBackdrop(e));
            return (bb.BuildDocument(doc), engine);
        }

        static Box FindBackdropSiblingOf(Box root, Element host) {
            // The backdrop is a BlockBox with Element == null whose immediate
            // next sibling is the host's box. Walk the tree to find that pair.
            foreach (var b in AllBoxes(root)) {
                for (int i = 0; i + 1 < b.Children.Count; i++) {
                    var maybeBackdrop = b.Children[i];
                    var maybeHost = b.Children[i + 1];
                    if (maybeBackdrop is BlockBox bb && bb.Element == null && bb.Style != null
                        && maybeHost.Element == host) {
                        return maybeBackdrop;
                    }
                }
            }
            return null;
        }

        [Test]
        public void Modal_dialog_gets_backdrop_sibling_before_host() {
            var (root, _) = BuildWithBackdrop(
                "<body><dialog id=\"d\" data-modal></dialog></body>",
                "dialog { display: block; } ::backdrop { background-color: red; }");
            Box hostBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.TagName == "dialog") { hostBox = b; break; }
            }
            Assert.That(hostBox, Is.Not.Null);
            var backdrop = FindBackdropSiblingOf(root, hostBox.Element);
            Assert.That(backdrop, Is.Not.Null, "modal dialog should produce a backdrop sibling");
            Assert.That(backdrop.Style.Get("background-color"), Is.EqualTo("red"));
        }

        [Test]
        public void Non_modal_open_dialog_gets_no_backdrop() {
            // <dialog open> without `data-modal` is the result of `Show()`, not
            // `ShowModal()`. Per spec, only modal dialogs get a top-layer
            // backdrop.
            var (root, _) = BuildWithBackdrop(
                "<body><dialog id=\"d\" open></dialog></body>",
                "dialog { display: block; } ::backdrop { background-color: red; }");
            Box hostBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.TagName == "dialog") { hostBox = b; break; }
            }
            Assert.That(hostBox, Is.Not.Null);
            var backdrop = FindBackdropSiblingOf(root, hostBox.Element);
            Assert.That(backdrop, Is.Null);
        }

        [Test]
        public void Closed_modal_dialog_gets_no_backdrop() {
            // No `data-modal` and display: none from UA -> not in box tree at
            // all. Even with the resolver wired up, BoxBuilder skips the host
            // (and therefore the backdrop) when display: none.
            var (root, _) = BuildWithBackdrop(
                "<body><dialog id=\"d\" style=\"display:none\" data-modal></dialog></body>");
            int dialogBoxes = 0;
            int backdropBoxes = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null && bb.Element.TagName == "dialog") dialogBoxes++;
                if (b is BlockBox bb2 && bb2.Element == null && bb2.Style != null
                    && bb2.Style.Get("position") == "fixed"
                    && bb2.Style.Get("top") == "0") backdropBoxes++;
            }
            Assert.That(dialogBoxes, Is.EqualTo(0));
            Assert.That(backdropBoxes, Is.EqualTo(0));
        }

        [Test]
        public void Open_popover_gets_backdrop_sibling_before_host() {
            var (root, _) = BuildWithBackdrop(
                "<body><div id=\"p\" popover data-popover-open></div></body>",
                "[popover] { display: block; } ::backdrop { background-color: blue; }");
            Box hostBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.HasAttribute("popover")) { hostBox = b; break; }
            }
            Assert.That(hostBox, Is.Not.Null);
            var backdrop = FindBackdropSiblingOf(root, hostBox.Element);
            Assert.That(backdrop, Is.Not.Null);
            Assert.That(backdrop.Style.Get("background-color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Backdrop_carries_viewport_filling_position_in_box_style() {
            var (root, _) = BuildWithBackdrop(
                "<body><dialog data-modal></dialog></body>",
                "dialog { display: block; }");
            Box hostBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.TagName == "dialog") { hostBox = b; break; }
            }
            var backdrop = FindBackdropSiblingOf(root, hostBox.Element);
            Assert.That(backdrop, Is.Not.Null);
            Assert.That(backdrop.Style.Get("position"), Is.EqualTo("fixed"));
            Assert.That(backdrop.Style.Get("top"), Is.EqualTo("0"));
            Assert.That(backdrop.Style.Get("right"), Is.EqualTo("0"));
            Assert.That(backdrop.Style.Get("bottom"), Is.EqualTo("0"));
            Assert.That(backdrop.Style.Get("left"), Is.EqualTo("0"));
            Assert.That(backdrop.Style.Get("display"), Is.EqualTo("block"));
        }

        [Test]
        public void Backdrop_appears_immediately_before_host_in_parent_children() {
            // Order matters for paint: backdrop must be at index < host so
            // BoxToPaintConverter visits it first.
            var (root, _) = BuildWithBackdrop(
                "<body><dialog data-modal></dialog></body>",
                "dialog { display: block; }");
            Box bodyBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.TagName == "body") { bodyBox = b; break; }
            }
            Assert.That(bodyBox, Is.Not.Null);
            int backdropIdx = -1, hostIdx = -1;
            for (int i = 0; i < bodyBox.Children.Count; i++) {
                var c = bodyBox.Children[i];
                if (c is BlockBox bb && bb.Element == null && bb.Style != null
                    && bb.Style.Get("position") == "fixed") backdropIdx = i;
                if (c.Element != null && c.Element.TagName == "dialog") hostIdx = i;
            }
            Assert.That(backdropIdx, Is.GreaterThanOrEqualTo(0));
            Assert.That(hostIdx, Is.GreaterThanOrEqualTo(0));
            Assert.That(backdropIdx, Is.LessThan(hostIdx));
            Assert.That(hostIdx - backdropIdx, Is.EqualTo(1));
        }

        [Test]
        public void No_backdrop_when_resolver_is_unwired() {
            // BoxBuilder constructed without backdropStyleOf must not synthesize
            // a backdrop even when the host element looks top-layer-eligible.
            var doc = HtmlParser.Parse("<body><dialog data-modal></dialog></body>");
            var sheets = new List<OriginatedStylesheet> { UA(BuiltinUserAgent), Author("dialog { display: block; }") };
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            var root = bb.BuildDocument(doc);
            int synth = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bx && bx.Element == null && bx.Style != null
                    && bx.Style.Get("position") == "fixed"
                    && bx.Style.Get("top") == "0") synth++;
            }
            Assert.That(synth, Is.EqualTo(0));
        }

        [Test]
        public void Resolver_returning_null_skips_backdrop() {
            // Some callers may want to selectively suppress backdrop synthesis
            // (e.g. accessibility tooling). Returning null from the resolver
            // is treated as "no backdrop for this host".
            var doc = HtmlParser.Parse("<body><dialog data-modal></dialog></body>");
            var sheets = new List<OriginatedStylesheet> { UA(BuiltinUserAgent), Author("dialog { display: block; }") };
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var bb = new BoxBuilder(
                e => styles.TryGetValue(e, out var cs) ? cs : null,
                _ => null);
            var root = bb.BuildDocument(doc);
            int synth = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bx && bx.Element == null && bx.Style != null
                    && bx.Style.Get("position") == "fixed") synth++;
            }
            Assert.That(synth, Is.EqualTo(0));
        }

        [Test]
        public void Two_open_modals_each_get_their_own_backdrop() {
            var (root, _) = BuildWithBackdrop(
                "<body><dialog id=\"a\" data-modal></dialog><dialog id=\"b\" data-modal></dialog></body>",
                "dialog { display: block; }");
            int backdrops = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element == null && bb.Style != null
                    && bb.Style.Get("position") == "fixed"
                    && bb.Style.Get("top") == "0") backdrops++;
            }
            Assert.That(backdrops, Is.EqualTo(2));
        }
    }
}
