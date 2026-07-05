// Tests for CSS Values L4 §6.2: `em` on font-size resolves against the
// PARENT element's computed font-size, not the element's own.
//
// The bug (H1-EM-FONTSIZE): InlineLayout.TryLayoutSingleRunFast was calling
//   StyleResolver.FontSizePx(style, container.Style, ctx)
// when style == container.Style (text run inheriting the container's style).
// That made the container its own em-parent, so UA `h1 { font-size: 2em }`
// resolved as 2 × h1's-own-size (32px) = 64px instead of 2 × parent (16px) = 32px.
//
// The fix: when source.Style == null (and style falls back to container.Style),
// pass container.Parent?.Style as the em-parent instead of container.Style.
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    /// <summary>
    /// CSS Values L4 §6.2 — em on font-size must resolve against the
    /// INHERITED (parent's computed) font-size, not the element's own.
    /// </summary>
    public class EmFontSizeResolutionTests {

        // --- Helper: find the first TextRun inside a named tag ---
        static TextRun FindFirstTextRun(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.TagName == tag) {
                    // Drill into its children for a TextRun.
                    foreach (var c in AllBoxes(b)) {
                        if (c is TextRun tr) return tr;
                    }
                }
            }
            return null;
        }

        static BlockBox FindFirstBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag)
                    return bb;
            }
            return null;
        }

        // --- 1. The primary regression: UA h1 { font-size: 2em } in 16px body ---
        // CSS Values L4 §6.2: 2em on font-size resolves against the *parent's*
        // computed font-size. body / main inherit 16px from html (UA default),
        // so h1's computed font-size must be 2 × 16 = 32px, NOT 2 × 32 = 64px.
        [Test]
        public void H1_ua_font_size_2em_resolves_against_parent_16px_not_own_32px() {
            // UA stylesheet contains `h1 { font-size: 2em }` (verified in
            // UserAgentStylesheet.cs line 43). BuiltinUserAgent (LayoutTestHelpers)
            // does NOT include this rule; we need the real UA sheet so we build
            // the cascade manually with UserAgentStylesheet.Parse().
            var doc = HtmlParser.Parse("<div><h1>Title</h1></div>");
            var sheets = new System.Collections.Generic.List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse()
            };
            var cascade = new CascadeEngine(sheets);
            var styles = cascade.ComputeAll(doc);
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = cascade.LastSnapshot,
                SnapshotStyles = cascade.Styles
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var s) ? s : null, ctx);

            // The text run inside h1 must be measured at 32px (2em × 16px parent),
            // not 64px (would be 2em × 32px own-size — the pre-fix bug value).
            // MonoFontMetrics: lineHeight(fs) = fs * 1.2; so h1 line-box height
            // at 32px → ~38.4px; at 64px (bug) → ~76.8px.
            var h1 = FindFirstBlock(root, "h1");
            Assert.That(h1, Is.Not.Null, "h1 block box must exist");

            var tr = FindFirstTextRun(root, "h1");
            Assert.That(tr, Is.Not.Null, "h1 must contain a TextRun");

            // CSS Values L4 §6.2 spec-correct: 2em × 16px = 32px.
            Assert.That(tr.FontSize, Is.EqualTo(32.0).Within(0.5),
                "h1 font-size: 2em must resolve to 32px (2 × parent 16px), not 64px");
        }

        // --- 2. Nested em compounding: parent 20px, child 1.5em → 30px ---
        // Each level's em resolves against that level's PARENT's computed size.
        [Test]
        public void Nested_em_font_size_compounds_correctly_against_parent() {
            var (root, _, _) = Build(
                "<div class='outer'><span class='inner'>text</span></div>",
                ".outer { font-size: 20px; } .inner { font-size: 1.5em; }",
                viewportWidth: 800);

            TextRun innerRun = null;
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Text == "text") { innerRun = tr; break; }
            }
            Assert.That(innerRun, Is.Not.Null, "inner text run must exist");
            // CSS Values L4 §6.2: 1.5em on .inner resolves against .outer's 20px → 30px.
            Assert.That(innerRun.FontSize, Is.EqualTo(30.0).Within(0.5),
                "span font-size: 1.5em must resolve to 30px (1.5 × parent 20px)");
        }

        // --- 3. em on OTHER properties uses the element's OWN font-size ---
        // CSS Values L4 §6.2 makes a special exception for font-size; all other
        // properties use the element's own computed font-size as the em base.
        // If h1 has font-size: 2em (→ 32px) and padding: 1em, padding must be
        // 32px (1 × h1's own 32px), NOT 16px (which would be 1 × parent).
        [Test]
        public void Em_on_non_font_size_property_uses_elements_own_font_size() {
            // Use a plain div so we control font-size exactly (avoid UA h1 ambiguity).
            var (root, _, _) = Build(
                "<div class='parent'><div class='child'>text</div></div>",
                ".parent { font-size: 16px; } .child { font-size: 2em; padding: 1em; }",
                viewportWidth: 800);

            var child = FindFirstBlock(root, "div");
            // child is the outer div; walk for the inner one with class child.
            BlockBox childBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox)
                        && bb.Element?.ClassName == "child") {
                    childBox = bb; break;
                }
            }
            Assert.That(childBox, Is.Not.Null, ".child box must exist");

            // .child font-size = 2em × 16px (parent) = 32px.
            // .child padding = 1em × 32px (.child's own font-size) = 32px.
            Assert.That(childBox.PaddingTop, Is.EqualTo(32.0).Within(0.5),
                "padding: 1em resolves against the element's own computed font-size (32px), not the parent's");
        }

        // --- 4. rem is unaffected — always roots against the root element ---
        // CSS Values L4 §6.2: rem resolves against the root element's font-size.
        // A nested element with an em-sized or large font-size must not disturb rem.
        [Test]
        public void Rem_resolves_against_root_font_size_regardless_of_em_nesting() {
            // RootFontSizePx = 16 in the Build helper. No matter what intermediate
            // em-sized elements say, rem stays at 16px.
            var (root, _, _) = Build(
                "<div class='parent'><div class='child'>text</div></div>",
                ".parent { font-size: 50px; } .child { font-size: 2rem; }",
                viewportWidth: 800);

            BlockBox childBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox)
                        && bb.Element?.ClassName == "child") {
                    childBox = bb; break;
                }
            }
            Assert.That(childBox, Is.Not.Null, ".child box must exist");

            // 2rem × 16px root = 32px regardless of .parent's 50px.
            TextRun remRun = null;
            foreach (var b in AllBoxes(childBox)) {
                if (b is TextRun tr) { remRun = tr; break; }
            }
            Assert.That(remRun, Is.Not.Null, "child text run must exist");
            Assert.That(remRun.FontSize, Is.EqualTo(32.0).Within(0.5),
                "font-size: 2rem resolves against root 16px → 32px, unaffected by parent's 50px");
        }

        // --- 5. h1 line-box height reflects the correct 32px font-size ---
        // MonoFontMetrics: lineHeight(fs) = fs * 1.2.  At 32px → 38.4px.
        // At 64px (pre-fix bug) → 76.8px. Validate the line-box so the
        // regression test pins the rendered geometry, not just TextRun.FontSize.
        [Test]
        public void H1_linebox_height_matches_32px_font_size_via_ua_2em() {
            var doc = HtmlParser.Parse("<h1>Hello</h1>");
            var sheets = new System.Collections.Generic.List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse()
            };
            var cascade = new CascadeEngine(sheets);
            var styles = cascade.ComputeAll(doc);
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = cascade.LastSnapshot,
                SnapshotStyles = cascade.Styles
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var s) ? s : null, ctx);

            LineBox lb = null;
            foreach (var b in AllBoxes(root)) {
                if (b is LineBox line) { lb = line; break; }
            }
            Assert.That(lb, Is.Not.Null, "h1 must produce a LineBox");

            // MonoFontMetrics.LineHeight(32) = 32 * 1.2 = 38.4px.
            // Pre-fix: LineHeight(64) = 64 * 1.2 = 76.8px.
            Assert.That(lb.Height, Is.EqualTo(32.0 * 1.2).Within(1.0),
                "h1 LineBox height must be ~38.4px (32px × 1.2), not ~76.8px (64px bug)");
        }
    }
}
