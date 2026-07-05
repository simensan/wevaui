using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class SnapshotCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        sealed class FakeState : IElementStateProvider {
            readonly Dictionary<Element, ElementState> map = new();
            long version = 1;
            public long Version => version;
            public void Set(Element e, ElementState s) { map[e] = s; version++; }
            public ElementState GetState(Element e) => map.TryGetValue(e, out var v) ? v : ElementState.None;
        }

        static IEnumerable<Element> AllElements(Node root) {
            if (root is Element e) yield return e;
            foreach (var c in root.Children) {
                foreach (var d in AllElements(c)) yield return d;
            }
        }

        static void AssertParity(IReadOnlyDictionary<Element, ComputedStyle> a,
                                 IReadOnlyDictionary<Element, ComputedStyle> b,
                                 string label = null) {
            Assert.That(b.Count, Is.EqualTo(a.Count), "computed style count differs " + (label ?? ""));
            foreach (var kv in a) {
                Assert.That(b.ContainsKey(kv.Key), "missing element in second result: " + kv.Key.TagName);
                var sa = kv.Value;
                var sb = b[kv.Key];
                foreach (var prop in sa.Enumerate()) {
                    sb.TryGet(prop.Key, out var v);
                    Assert.That(v, Is.EqualTo(prop.Value), $"{label ?? ""} mismatch on <{kv.Key.TagName}> '{prop.Key}'");
                }
                foreach (var prop in sb.Enumerate()) {
                    sa.TryGet(prop.Key, out var v);
                    Assert.That(v, Is.EqualTo(prop.Value), $"{label ?? ""} reverse mismatch on <{kv.Key.TagName}> '{prop.Key}'");
                }
            }
        }

        static (CascadeEngine snap, CascadeEngine mgd) Engines(string css, MediaContext? mc = null) {
            var sheet = Author(css);
            var snap = mc.HasValue
                ? new CascadeEngine(new[] { sheet }, mc.Value, true)
                : new CascadeEngine(new[] { sheet }, true);
            var mgd = mc.HasValue
                ? new CascadeEngine(new[] { sheet }, mc.Value, false)
                : new CascadeEngine(new[] { sheet }, false);
            return (snap, mgd);
        }

        [Test]
        public void Empty_document_both_paths_match() {
            var doc = new Document();
            var (snap, mgd) = Engines("body { color: red; }");
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
        }

        [Test]
        public void Single_element_single_rule_parity() {
            var doc = Html("<div id=\"x\"></div>");
            var (snap, mgd) = Engines("#x { color: red; }");
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
        }

        [Test]
        public void Mid_size_50_elements_30_rules_full_parity() {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            for (int p = 0; p < 5; p++) {
                sb.Append("<div class=\"panel zone-").Append(p % 3).Append("\">");
                for (int i = 0; i < 9; i++) {
                    bool sel = (i + p) % 4 == 0;
                    string cls = sel ? "item selected" : "item";
                    sb.Append("<span class=\"").Append(cls).Append("\"");
                    if (i == 0) sb.Append(" id=\"e").Append(p).Append('"');
                    sb.Append(">x</span>");
                }
                sb.Append("</div>");
            }
            sb.Append("</section>");
            var doc = Html(sb.ToString());

            string css =
                "div { color: navy; font-size: 14px; }" +
                "span { color: dimgray; }" +
                "section { color: black; }" +
                ".container { background-color: white; }" +
                ".panel { font-size: 12px; }" +
                ".panel.zone-0 { color: green; }" +
                ".panel.zone-1 { color: blue; }" +
                ".panel.zone-2 { color: orange; }" +
                ".item { font-style: italic; }" +
                ".selected { color: rebeccapurple; }" +
                ".item.selected { font-weight: bold; }" +
                "#root { font-family: serif; }" +
                "#e0 { color: red; }" +
                "#e1 { color: lime; }" +
                "section .panel { display: block; }" +
                "section > div { padding: 4px; }" +
                ".container .panel { margin: 2px; }" +
                "div .item { text-align: left; }" +
                "div span.item { letter-spacing: 1px; }" +
                "section .selected { text-decoration: underline; }" +
                "* { box-sizing: border-box; }" +
                ".container > .panel.zone-0 { border: 1px solid black; }" +
                ".panel .item { line-height: 1.2; }" +
                "div.panel { vertical-align: top; }" +
                "section span { display: inline; }" +
                ".zone-0 .item { opacity: 1; }" +
                ".zone-1 .item { opacity: 0.9; }" +
                ".zone-2 .item { opacity: 0.8; }" +
                "div .selected { font-variant: normal; }" +
                ".item:first-child { margin-left: 0; }" +
                "span:not(.item) { display: none; }";

            var (snap, mgd) = Engines(css);
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
        }

        [Test]
        public void Sibling_combinator_managed_verifier_gates_correctly() {
            var doc = Html("<div><span id=\"a\"></span><span id=\"b\"></span></div>");
            var (snap, mgd) = Engines("span + span { color: red; }");
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
            Assert.That(snap.ComputeAll(doc)[doc.GetElementById("b")].Get("color"), Is.EqualTo("red"));
            Assert.That(snap.ComputeAll(doc)[doc.GetElementById("a")].Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Hover_state_drives_both_paths_identically() {
            var doc = Html("<button id=\"b\">go</button>");
            var sheet = Author("button { color: black; } button:hover { color: red; }");
            var snap = new CascadeEngine(new[] { sheet }, true);
            var mgd = new CascadeEngine(new[] { sheet }, false);
            var btn = doc.GetElementById("b");

            var s1 = new FakeState();
            var m1 = new FakeState();
            AssertParity(snap.ComputeAll(doc, s1), mgd.ComputeAll(doc, m1), "idle");

            s1.Set(btn, ElementState.Hover);
            m1.Set(btn, ElementState.Hover);
            AssertParity(snap.ComputeAll(doc, s1), mgd.ComputeAll(doc, m1), "hover");
        }

        [Test]
        public void Nth_child_managed_verifier_gates_correctly() {
            var doc = Html("<ul><li>a</li><li>b</li><li>c</li><li>d</li><li>e</li></ul>");
            var (snap, mgd) = Engines("li:nth-child(2n+1) { color: red; }");
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
        }

        [Test]
        public void Not_pseudo_managed_verifier_gates_correctly() {
            var doc = Html("<div><span class=\"x\"></span><span></span><span class=\"x\"></span></div>");
            var (snap, mgd) = Engines("span:not(.x) { color: red; }");
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
        }

        [Test]
        public void Inline_style_works_in_both_modes() {
            var doc = Html("<div id=\"x\" style=\"color: green; font-size: 24px;\"></div>");
            var (snap, mgd) = Engines("#x { color: red; }");
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
            Assert.That(snap.ComputeAll(doc)[doc.GetElementById("x")].Get("color"), Is.EqualTo("green"));
        }

        [Test]
        public void Custom_property_and_var_resolve_identically() {
            var doc = Html("<section><div><span id=\"x\"></span></div></section>");
            var (snap, mgd) = Engines("section { --accent: rebeccapurple; } #x { color: var(--accent); }");
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
            Assert.That(snap.ComputeAll(doc)[doc.GetElementById("x")].Get("color"), Is.EqualTo("rebeccapurple"));
        }

        [Test]
        public void Media_gating_applies_in_both_modes() {
            var doc = Html("<div id=\"x\"></div>");
            string css = "@media (min-width: 600px) { #x { color: red; } } @media (max-width: 599px) { #x { color: blue; } }";

            var (snap, mgd) = Engines(css, MediaContext.Default(400, 400));
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc), "narrow");
            Assert.That(snap.ComputeAll(doc)[doc.GetElementById("x")].Get("color"), Is.EqualTo("blue"));

            snap.MediaContext = MediaContext.Default(800, 800);
            mgd.MediaContext = MediaContext.Default(800, 800);
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc), "wide");
            Assert.That(snap.ComputeAll(doc)[doc.GetElementById("x")].Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Cache_hit_count_after_warm_pass_matches_managed() {
            var doc = Html("<section><div id=\"a\"></div><div id=\"b\"></div><div id=\"c\"></div></section>");
            var sheet = Author("div { color: red; }");
            var snap = new CascadeEngine(new[] { sheet }, true);
            var mgd = new CascadeEngine(new[] { sheet }, false);

            snap.ComputeAll(doc);
            mgd.ComputeAll(doc);

            snap.ResetCacheStats();
            mgd.ResetCacheStats();

            int snapCount = snap.ComputeAll(doc).Count;
            int mgdCount = mgd.ComputeAll(doc).Count;

            Assert.That(snapCount, Is.EqualTo(mgdCount));
            Assert.That(snap.CacheHits, Is.EqualTo(mgd.CacheHits));
            Assert.That(snap.CacheMisses, Is.EqualTo(mgd.CacheMisses));
            Assert.That(snap.CacheMisses, Is.EqualTo(0));
        }

        [Test]
        public void Mutation_then_recompute_both_paths_converge() {
            var doc = Html("<div id=\"r\"><span id=\"a\"></span><span id=\"b\"></span></div>");
            var (snap, mgd) = Engines(".x { color: red; } div { color: black; }");

            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc), "before");

            doc.GetElementById("a").SetAttribute("class", "x");
            doc.GetElementById("b").SetAttribute("class", "x");

            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc), "after");
            Assert.That(snap.ComputeAll(doc)[doc.GetElementById("a")].Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Inheritance_child_inherits_color_in_both_modes() {
            var doc = Html("<div><section><span id=\"x\"></span></section></div>");
            var (snap, mgd) = Engines("div { color: rebeccapurple; }");
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
            Assert.That(snap.ComputeAll(doc)[doc.GetElementById("x")].Get("color"), Is.EqualTo("rebeccapurple"));
        }

        [Test]
        public void Hundred_element_fixture_cache_miss_count_matches_managed() {
            var doc = new Document();
            var root = new Element("div");
            doc.AppendChild(root);
            var elements = new List<Element> { root };
            for (int i = 1; i < 100; i++) {
                var e = new Element(i % 3 == 0 ? "span" : "div");
                if (i % 5 == 0) e.SetAttribute("class", "x");
                if (i % 7 == 0) e.SetAttribute("id", "id" + i);
                elements[(i - 1) / 2].AppendChild(e);
                elements.Add(e);
            }
            string css = ".x { color: red; } div { font-size: 16px; } span { font-size: 14px; } #id7 { color: green; }";
            var sheet = Author(css);
            var snap = new CascadeEngine(new[] { sheet }, true);
            var mgd = new CascadeEngine(new[] { sheet }, false);

            snap.ComputeAll(doc);
            mgd.ComputeAll(doc);
            Assert.That(snap.CacheMisses, Is.EqualTo(mgd.CacheMisses));

            snap.ResetCacheStats();
            mgd.ResetCacheStats();
            snap.ComputeAll(doc);
            mgd.ComputeAll(doc);
            Assert.That(snap.CacheMisses, Is.EqualTo(0));
            Assert.That(mgd.CacheMisses, Is.EqualTo(0));
            Assert.That(snap.CacheHits, Is.EqualTo(mgd.CacheHits));
        }

        [Test]
        public void Snapshot_disabled_engine_falls_back_to_managed_path() {
            var doc = Html("<div id=\"x\" class=\"c\"><span class=\"y\"></span></div>");
            string css = ".c { color: red; } .y { color: blue; }";
            var snap = new CascadeEngine(new[] { Author(css) }, true);
            var mgd = new CascadeEngine(new[] { Author(css) }, false);
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
            Assert.That(snap.UseSnapshot, Is.True);
            Assert.That(mgd.UseSnapshot, Is.False);
        }

        [Test]
        public void Pseudo_element_selectors_are_filtered_out_in_both_modes() {
            // ::before never matches a runtime element. Both modes must skip it; the
            // sibling regular `p` rule must still apply.
            var doc = Html("<p id=\"x\">hi</p>");
            var (snap, mgd) = Engines("p::before { content: 'A'; } p { color: red; }");
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
            Assert.That(snap.ComputeAll(doc)[doc.GetElementById("x")].Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Snapshot_built_once_per_ComputeAll_not_per_element() {
            var doc = Html("<div><span></span><span></span><span></span><span></span><span></span></div>");
            var sheet = Author("span { color: red; }");
            var snap = new CascadeEngine(new[] { sheet }, true);
            Assert.That(snap.SnapshotBuildCount, Is.EqualTo(0));
            snap.ComputeAll(doc);
            Assert.That(snap.SnapshotBuildCount, Is.EqualTo(1), "snapshot logical-build counted once per ComputeAll, not per element");
            // v0.5 the engine reuses the snapshot when the document hasn't
            // mutated since the last call; the counter still increments per
            // ComputeAll pass to keep logical observability of the cascade
            // pipeline.
            snap.ComputeAll(doc);
            Assert.That(snap.SnapshotBuildCount, Is.EqualTo(2), "logical build count increments per ComputeAll pass");
        }

        [Test]
        public void CompiledRulesIndex_reused_across_calls() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var sheet = Author("div { color: red; }");
            var snap = new CascadeEngine(new[] { sheet }, true);
            for (int i = 0; i < 10; i++) snap.ComputeAll(doc);
            Assert.That(snap.IndexBuildCount, Is.EqualTo(1));
        }

        [Test]
        public void Single_element_Compute_uses_managed_path_regardless_of_flag() {
            var doc = Html("<div id=\"x\"><span id=\"y\"></span></div>");
            var sheet = Author("span { color: red; } div { color: navy; }");
            var snap = new CascadeEngine(new[] { sheet }, true);
            var mgd = new CascadeEngine(new[] { sheet }, false);
            var s1 = snap.Compute(doc.GetElementById("y"));
            var m1 = mgd.Compute(doc.GetElementById("y"));
            Assert.That(s1.Get("color"), Is.EqualTo(m1.Get("color")));
            Assert.That(s1.Get("color"), Is.EqualTo("red"));
            // Single Compute() does not build a snapshot.
            Assert.That(snap.IndexBuildCount, Is.EqualTo(0));
        }

        [Test]
        public void Important_inline_vs_important_author_parity() {
            var doc = Html("<div id=\"x\" style=\"color: green !important;\"></div>");
            var (snap, mgd) = Engines("#x { color: red !important; }");
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
        }

        [Test]
        public void Origin_ordering_parity_across_UA_User_Author() {
            var doc = Html("<div id=\"x\"></div>");
            var ua = OriginatedStylesheet.UserAgent(Css("#x { color: gray; }"));
            var user = OriginatedStylesheet.User(Css("#x { color: blue; }"));
            var author = OriginatedStylesheet.Author(Css("#x { color: red; }"));
            var snap = new CascadeEngine(new[] { ua, user, author }, true);
            var mgd = new CascadeEngine(new[] { ua, user, author }, false);
            AssertParity(snap.ComputeAll(doc), mgd.ComputeAll(doc));
        }
    }
}
