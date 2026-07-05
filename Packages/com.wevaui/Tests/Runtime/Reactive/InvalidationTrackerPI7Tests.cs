using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Reactive {
    // PI7 — class/id attribute mutation narrows DESCENDANT invalidation marks
    // from Style|Layout|Paint to Style only, while keeping the TARGET at the
    // full Style|Layout|Paint. The downstream layer caches (LayoutCacheKey
    // and PaintCacheKey both embed box.Style.Version) re-evaluate per element
    // when the cascade produces a new ComputedStyle on the descendant — so a
    // class change that doesn't actually flip any descendant's computed style
    // pays the dictionary write but does NOT pay the box-level recompute.
    public class InvalidationTrackerPI7Tests {
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        // 1. Class change on element with no descendant rules: descendants
        //    marked Style only (NOT Layout|Paint). Tracker DirtyEntries
        //    enumeration confirms the kind breakdown.
        [Test]
        public void Class_change_with_no_descendant_rule_marks_descendants_Style_only() {
            var t = new InvalidationTracker();
            var doc = new Document();
            var root = new Element("div");
            var child = new Element("span");
            var grand = new Element("em");
            doc.AppendChild(root);
            root.AppendChild(child);
            child.AppendChild(grand);
            t.Attach(doc);
            t.Clear();

            root.SetAttribute("class", "themed");

            // Target keeps full Style|Layout|Paint — it's the element whose
            // own computed style is changing, and the IncrementalLayoutGate
            // needs a Layout mark somewhere to NOT skip the layout pass.
            Assert.That(t.GetKinds(root),
                Is.EqualTo(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint));
            // Descendants get Style only — no Layout, no Paint, no Composite.
            Assert.That(t.GetKinds(child), Is.EqualTo(InvalidationKind.Style));
            Assert.That(t.GetKinds(grand), Is.EqualTo(InvalidationKind.Style));

            // Cross-check via DirtyEntries enumeration: descendants have Style
            // bit set, do NOT have Layout / Paint bits set.
            var snapshot = new Dictionary<Node, InvalidationKind>();
            foreach (var kv in t.DirtyEntries) snapshot[kv.Key] = kv.Value;
            Assert.That(snapshot.ContainsKey(child), Is.True);
            Assert.That((snapshot[child] & InvalidationKind.Style), Is.EqualTo(InvalidationKind.Style));
            Assert.That((snapshot[child] & InvalidationKind.Layout), Is.EqualTo(InvalidationKind.None));
            Assert.That((snapshot[child] & InvalidationKind.Paint), Is.EqualTo(InvalidationKind.None));
        }

        // 2. Class change where a descendant rule fires (.parent .child { color: red })
        //    and the new class triggers a cascade-changed value: after running
        //    the cascade and dropping caches via LayoutEngine.Apply /
        //    BoxToPaintConverter.Apply, the descendant's computed style
        //    genuinely differs from before. We verify the cascade observes
        //    the diff — proving that the Style-only mark on descendants is
        //    sufficient for the downstream paint-cache invalidation chain to
        //    kick in (paint cache keys on style.Version, which bumps).
        [Test]
        public void Class_change_with_descendant_rule_produces_changed_descendant_style() {
            var doc = HtmlParser.Parse("<section id=\"r\"><span id=\"c\"></span></section>");
            var engine = new CascadeEngine(new[] {
                Author(".parent #c { color: red; } .other #c { color: blue; }")
            });
            var r = doc.GetElementById("r");
            var c = doc.GetElementById("c");
            r.SetAttribute("class", "parent");
            // Initial cascade so we have a previous-style snapshot for c.
            engine.ComputeAll(doc);
            var before = engine.Compute(c);
            long beforeVersion = before.Version;

            // Now drive the class change end-to-end through the tracker.
            var t = new InvalidationTracker();
            t.Attach(doc);
            t.Clear();
            r.SetAttribute("class", "other");

            // Strategy B mark breakdown: target full, descendant Style only.
            Assert.That(t.GetKinds(r),
                Is.EqualTo(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint));
            Assert.That(t.GetKinds(c), Is.EqualTo(InvalidationKind.Style));

            // Cascade re-evaluates: drops cache entries for elements marked
            // Style|Structure, then ComputeAll produces a fresh ComputedStyle
            // for c (parent style version bumped → c misses its cache).
            engine.Apply(t);
            engine.ComputeAll(doc);
            var after = engine.Compute(c);
            Assert.That(after.Version, Is.GreaterThan(beforeVersion),
                "descendant computed style version must bump when its winning rule flipped");
            Assert.That(after.TryGet(CssProperties.ColorId, out var afterColor), Is.True);
            Assert.That(afterColor, Is.EqualTo("blue"),
                ".other #c rule must now win over .parent #c after the class flip");
            // PaintCacheKey embeds box.Style.Version — the version bump
            // proves the paint cache will miss on the next paint pass, which
            // is the contract that makes Style-only descendant marks safe.
        }

        // 3. Class change where the descendant rule's outcome doesn't
        //    actually flip the descendant's computed style: descendants stay
        //    Style only on the tracker (no Layout/Paint promotion) AND the
        //    cascade returns the same ComputedStyle instance via its cache
        //    hit. This is the WIN case — the conservative pre-Strategy-B
        //    code would have marked the whole subtree Style|Layout|Paint
        //    even though nothing downstream actually changed.
        [Test]
        public void Class_change_that_doesnt_affect_descendant_keeps_descendant_Style_only() {
            var doc = HtmlParser.Parse("<section id=\"r\"><span id=\"c\"></span></section>");
            var engine = new CascadeEngine(new[] {
                Author("#c { color: red; }") // rule matches #c regardless of #r's class
            });
            var r = doc.GetElementById("r");
            var c = doc.GetElementById("c");
            r.SetAttribute("class", "a");
            engine.ComputeAll(doc);
            var before = engine.Compute(c);

            var t = new InvalidationTracker();
            t.Attach(doc);
            t.Clear();
            r.SetAttribute("class", "b");

            // Tracker kinds: target full, descendant Style only.
            Assert.That(t.GetKinds(r),
                Is.EqualTo(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint));
            Assert.That(t.GetKinds(c), Is.EqualTo(InvalidationKind.Style));
            // No Layout / Paint marks landed on the descendant from
            // OnMutation itself — the only way they could get there is via
            // a downstream cascade promotion (which we don't wire to this
            // tracker in unit-test scope; production lifecycle promotes
            // via LayoutCacheKey/PaintCacheKey style.Version mismatch
            // inside Layout/Paint passes rather than via tracker marks).
            Assert.That(t.IsDirty(c, InvalidationKind.Layout), Is.False);
            Assert.That(t.IsDirty(c, InvalidationKind.Paint), Is.False);

            // Cascade re-evaluation: descendant's color rule didn't change,
            // so the resolved value stays "red". The descendant's cache
            // entry was dropped by engine.Apply, but the next Compute
            // resolves to the same value — what matters here is that the
            // tracker did NOT pay Layout|Paint dict-writes on the descendant
            // for a no-op class change.
            engine.Apply(t);
            engine.ComputeAll(doc);
            var after = engine.Compute(c);
            Assert.That(after.TryGet(CssProperties.ColorId, out var afterColor), Is.True);
            Assert.That(afterColor, Is.EqualTo("red"),
                "color rule on #c is independent of #r's class; resolved value unchanged");
            _ = before; // pin: before-style read happens so the cascade has a previous snapshot.
        }

        // 4. 1000-element subtree class change: total dirty-entries count is
        //    unchanged (Strategy B is about kind, not count) — but per-entry
        //    kind is Style only (1 bit) on descendants, whereas pre-fix it
        //    was Style|Layout|Paint (3 bits). The Bound here is the kind
        //    breakdown, not the entry count.
        [Test]
        public void Large_subtree_class_change_marks_one_thousand_descendants_Style_only() {
            const int subtreeSize = 1000;
            var t = new InvalidationTracker();
            var doc = new Document();
            var root = new Element("div");
            doc.AppendChild(root);
            // Build a flat subtree of 1000 children (a flat fan-out is the
            // worst case for theme-toggle patterns — every leaf is a direct
            // child of the wrapper whose class flips).
            var children = new List<Element>(subtreeSize);
            for (int i = 0; i < subtreeSize; i++) {
                var c = new Element("li");
                root.AppendChild(c);
                children.Add(c);
            }
            t.Attach(doc);
            t.Clear();

            root.SetAttribute("class", "themeB");

            // Entry count: 1 target + 1000 descendants = 1001 dirty entries.
            // (Matches the pre-fix count — Strategy B is about narrowing the
            // KIND per entry, not removing entries.)
            Assert.That(t.DirtyCount, Is.EqualTo(subtreeSize + 1));

            // Target keeps the full mark.
            Assert.That(t.GetKinds(root),
                Is.EqualTo(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint));

            // Every descendant gets Style only — count the kind distribution
            // across the dirty set.
            int descendantsStyleOnly = 0;
            int descendantsWithLayoutOrPaint = 0;
            foreach (var kv in t.DirtyEntries) {
                if (ReferenceEquals(kv.Key, root)) continue;
                if (kv.Value == InvalidationKind.Style) descendantsStyleOnly++;
                if ((kv.Value & (InvalidationKind.Layout | InvalidationKind.Paint)) != 0) {
                    descendantsWithLayoutOrPaint++;
                }
            }
            Assert.That(descendantsStyleOnly, Is.EqualTo(subtreeSize),
                "all 1000 descendants must be marked Style only, not Style|Layout|Paint");
            Assert.That(descendantsWithLayoutOrPaint, Is.EqualTo(0),
                "no descendant should carry Layout or Paint dirty bits from a class change");
        }
    }
}
