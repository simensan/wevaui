using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Layout.Scrolling {
    // Audit LY4: shrink-to-fit probes (RelayoutContentAt) start by calling
    // UnwrapLineBoxes, which recursed into EVERY descendant BlockBox with no
    // ReuseContent check — including a grafted scroll container's frozen
    // content. LayoutContent then early-returns at the graft, so the
    // unwrapped lines were NEVER re-laid: the classic scrollable dropdown
    // (`position:absolute; width:auto` wrapper around an overflow list)
    // had its text layout shredded into raw coalesced TextRuns on any full
    // layout with dirt elsewhere.
    public class ScrollGraftProbeDestructionTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        const string Html =
            "<div class=\"page\">"
            + "<div class=\"anim\">grows</div>"
            + "<div class=\"dd\">"                       // abs-pos width:auto wrapper -> probes
            +   "<div class=\"list\">"                   // scroll container -> grafted
            +     "<div class=\"item\">alpha beta gamma</div>"
            +     "<div class=\"item\">delta epsilon zeta</div>"
            +   "</div>"
            + "</div>"
            + "</div>";

        const string Css = @"
            * { box-sizing: border-box; }
            .page { position: relative; width: 800px; }
            .anim { height: 20px; }
            .dd { position: absolute; top: 30px; left: 10px; }
            .list { width: 200px; height: 120px; overflow-y: auto; }
            .item { height: 24px; font-size: 14px; }";

        [Test]
        public void Shrink_probe_on_abs_wrapper_does_not_shred_grafted_text() {
            bool saved = LayoutEngine.EnableScrollBoundaryReuse;
            try {
                LayoutEngine.EnableScrollBoundaryReuse = true;
                var doc = HtmlParser.Parse(Html);
                var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                    UserAgentStylesheet.Parse(), Author(Css),
                });
                engine.ComputeAll(doc);
                var styles = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
                var ctx = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 1000, ViewportHeightPx = 700,
                    RootFontSizePx = 16, DpiPixelsPerInch = 96,
                    Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
                };
                var le = new LayoutEngine(new MonoFontMetrics());
                var tracker = new InvalidationTracker();
                tracker.Attach(doc);
                System.Func<Element, ComputedStyle> styleOf =
                    e => styles.TryGetValue(e, out var cs) ? cs : null;

                Element anim = null;
                foreach (var e in engine.ResultMap.Keys) {
                    if (e.GetAttribute("class") == "anim") { anim = e; break; }
                }
                Assert.That(anim, Is.Not.Null);

                var root = le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
                AssertItemsHaveLineBoxes(root, "after the first (fresh) layout");

                // Full layouts with dirt OUTSIDE the dropdown: the .list is
                // clean -> grafted; the fresh .dd wrapper's shrink-to-fit
                // probe then walks into the graft.
                for (int frame = 0; frame < 3; frame++) {
                    styles[anim].Set("height", (22 + frame * 2) + "px");
                    tracker.MarkLayoutForElement(anim, styleOf);
                    tracker.MarkDirty(anim, InvalidationKind.Paint);
                    tracker.MarkDirty(doc, InvalidationKind.Structure);
                    ctx.SnapshotStyles = null;
                    root = le.Layout(doc, styleOf, ctx, tracker);
                    tracker.Clear();
                    AssertItemsHaveLineBoxes(root, $"after grafted full layout {frame + 1}");
                }
            } finally {
                LayoutEngine.EnableScrollBoundaryReuse = saved;
            }
        }

        static string Dump(Box b, int depth = 0) {
            var sb = new System.Text.StringBuilder();
            sb.Append(new string(' ', depth * 2))
              .Append(b.GetType().Name)
              .Append(" <").Append(b.Element?.TagName).Append(" ").Append(b.Element?.ClassName).Append(">")
              .Append($" X={b.X:0.#} Y={b.Y:0.#} W={b.Width:0.#} H={b.Height:0.#} reuse={b.ReuseContent}\n");
            foreach (var c in b.ChildList) sb.Append(Dump(c, depth + 1));
            return sb.ToString();
        }

        static void AssertItemsHaveLineBoxes(Box root, string when) {
            int items = 0;
            void Walk(Box b) {
                // BlockBox only — TextRuns/LineBoxes carry the same Element.
                if (b is BlockBox && b.Element != null && (b.Element.ClassName ?? "") == "item") {
                    items++;
                    Assert.That(b.ChildList.Count, Is.GreaterThan(0),
                        $"{when}: item must have content\nTREE:\n{Dump(root)}");
                    Assert.That(b.ChildList[0], Is.InstanceOf<LineBox>(),
                        $"{when}: item text must stay wrapped in LineBoxes — a bare TextRun means " +
                        "the shrink probe unwrapped frozen graft content that was never re-laid (audit LY4)");
                }
                foreach (var c in b.ChildList) Walk(c);
            }
            Walk(root);
            Assert.That(items, Is.EqualTo(2), $"{when}: both items present");
        }
    }
}
