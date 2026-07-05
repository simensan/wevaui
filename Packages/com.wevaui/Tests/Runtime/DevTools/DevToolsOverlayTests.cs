using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Css.Cascade;
using Weva.DevTools;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Paint.Conversion;
using Weva.Reactive;

namespace Weva.Tests.DevTools {
    public class DevToolsOverlayTests {
        [Test]
        public void DevToolsOverlay_default_toggle_key_is_F12() {
            // Contract: README + menu.html sample tell users to press F12.
            // The MonoBehaviour's serialized default must agree.
            var go = new GameObject("dt-overlay-default-key");
            try {
                var overlay = go.AddComponent<DevToolsOverlay>();
                Assert.That(overlay.ToggleKey, Is.EqualTo(KeyCode.F12));
                Assert.That(overlay.Enabled, Is.True);
                Assert.That(overlay.Mode, Is.EqualTo(OverlayMode.All));
            } finally {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DevToolsOverlay_setters_round_trip() {
            // Authors flip these from inspector and code paths. Guard against
            // a future refactor that swaps the backing field for a property
            // with side effects.
            var go = new GameObject("dt-overlay-setters");
            try {
                var overlay = go.AddComponent<DevToolsOverlay>();
                overlay.Enabled = false;
                overlay.ToggleKey = KeyCode.F11;
                overlay.Mode = OverlayMode.Outlines | OverlayMode.Performance;
                Assert.That(overlay.Enabled, Is.False);
                Assert.That(overlay.ToggleKey, Is.EqualTo(KeyCode.F11));
                Assert.That((overlay.Mode & OverlayMode.DirtyTracking) == 0);
                Assert.That((overlay.Mode & OverlayMode.Outlines) != 0);
            } finally {
                Object.DestroyImmediate(go);
            }
        }

        // These helpers build BARE boxes (no Element) to pin the box-model
        // rect math in isolation. BoxOutlineRenderer.SkipAnonymousBoxes
        // (default true -- production noise filter for element-less wrappers)
        // would skip every one of them, so all renderer instances in this
        // file opt out of the filter explicitly.
        static BlockBox MakeBox(double x, double y, double w, double h) {
            var b = new BlockBox();
            b.X = x; b.Y = y; b.Width = w; b.Height = h;
            return b;
        }

        static BlockBox MakeBoxWithMarginPaddingBorder(double x, double y, double w, double h,
            double m, double p, double border) {
            var b = MakeBox(x, y, w, h);
            b.MarginTop = b.MarginRight = b.MarginBottom = b.MarginLeft = m;
            b.PaddingTop = b.PaddingRight = b.PaddingBottom = b.PaddingLeft = p;
            b.BorderTop = b.BorderRight = b.BorderBottom = b.BorderLeft = border;
            return b;
        }

        [Test]
        public void OverlayMode_All_includes_every_individual_flag() {
            var all = OverlayMode.All;
            Assert.That((all & OverlayMode.Outlines) != 0);
            Assert.That((all & OverlayMode.DirtyTracking) != 0);
            Assert.That((all & OverlayMode.Performance) != 0);
        }

        [Test]
        public void OverlayMode_Off_is_zero() {
            Assert.That((int)OverlayMode.Off, Is.EqualTo(0));
        }

        [Test]
        public void OverlayMode_individual_flags_compose() {
            var subset = OverlayMode.Outlines | OverlayMode.Performance;
            Assert.That((subset & OverlayMode.Outlines) != 0);
            Assert.That((subset & OverlayMode.Performance) != 0);
            Assert.That((subset & OverlayMode.DirtyTracking) == 0);
        }

        [Test]
        public void BoxOutlineRenderer_emits_four_rects_per_box() {
            var renderer = new BoxOutlineRenderer { SkipAnonymousBoxes = false };
            var box = MakeBoxWithMarginPaddingBorder(10, 20, 100, 50, 8, 4, 2);
            var rects = new List<OverlayRect>();
            int count = renderer.EmitInto(box, rects);
            Assert.That(count, Is.EqualTo(4));
            Assert.That(rects[0].Kind, Is.EqualTo(OverlayRectKind.Margin));
            Assert.That(rects[1].Kind, Is.EqualTo(OverlayRectKind.Border));
            Assert.That(rects[2].Kind, Is.EqualTo(OverlayRectKind.Padding));
            Assert.That(rects[3].Kind, Is.EqualTo(OverlayRectKind.Content));
        }

        [Test]
        public void BoxOutlineRenderer_margin_rect_extends_outside_border_box() {
            var renderer = new BoxOutlineRenderer { SkipAnonymousBoxes = false };
            // border-box dims, margin 8 in every direction.
            var box = MakeBoxWithMarginPaddingBorder(10, 20, 100, 50, 8, 0, 0);
            var rects = renderer.Emit(box);
            var margin = rects[0];
            Assert.That(margin.X, Is.EqualTo(2).Within(1e-6));
            Assert.That(margin.Y, Is.EqualTo(12).Within(1e-6));
            Assert.That(margin.Width, Is.EqualTo(116).Within(1e-6));
            Assert.That(margin.Height, Is.EqualTo(66).Within(1e-6));
        }

        [Test]
        public void BoxOutlineRenderer_content_rect_excludes_padding_and_border() {
            var renderer = new BoxOutlineRenderer { SkipAnonymousBoxes = false };
            var box = MakeBoxWithMarginPaddingBorder(0, 0, 100, 100, 0, 4, 2);
            var rects = renderer.Emit(box);
            var content = rects[3];
            Assert.That(content.Kind, Is.EqualTo(OverlayRectKind.Content));
            Assert.That(content.X, Is.EqualTo(6).Within(1e-6));
            Assert.That(content.Y, Is.EqualTo(6).Within(1e-6));
            Assert.That(content.Width, Is.EqualTo(88).Within(1e-6));
            Assert.That(content.Height, Is.EqualTo(88).Within(1e-6));
        }

        [Test]
        public void BoxOutlineRenderer_skips_TextRun_decoration() {
            var renderer = new BoxOutlineRenderer { SkipAnonymousBoxes = false };
            var parent = MakeBoxWithMarginPaddingBorder(0, 0, 100, 50, 0, 0, 0);
            var text = new TextRun();
            text.Text = "hello";
            text.X = 0; text.Y = 0; text.Width = 50; text.Height = 16;
            parent.AddChild(text);
            var rects = renderer.Emit(parent);
            // Parent box only — text run is skipped.
            Assert.That(rects.Count, Is.EqualTo(4));
        }

        [Test]
        public void BoxOutlineRenderer_recurses_into_children_with_absolute_offsets() {
            var renderer = new BoxOutlineRenderer { SkipAnonymousBoxes = false };
            var parent = MakeBox(10, 10, 100, 100);
            var child = MakeBox(5, 5, 20, 20);
            parent.AddChild(child);
            var rects = renderer.Emit(parent);
            Assert.That(rects.Count, Is.EqualTo(8));
            // Child's border rect (index 5: parent margin/border/padding/content =0..3, child margin=4, border=5)
            var childBorder = rects[5];
            Assert.That(childBorder.Kind, Is.EqualTo(OverlayRectKind.Border));
            Assert.That(childBorder.X, Is.EqualTo(15).Within(1e-6));
            Assert.That(childBorder.Y, Is.EqualTo(15).Within(1e-6));
        }

        [Test]
        public void DirtyHighlighter_classifies_layout_change_as_layout() {
            var tracker = new InvalidationTracker();
            var e = new Element("div");
            tracker.MarkDirty(e, InvalidationKind.Layout | InvalidationKind.Paint);
            var h = new DirtyHighlighter();
            h.CaptureFrame(tracker);
            Assert.That(h.Active.ContainsKey(e));
            Assert.That(h.Active[e].Kind, Is.EqualTo(DirtyHighlightKind.Layout));
        }

        [Test]
        public void DirtyHighlighter_classifies_paint_only_change_as_paint() {
            var tracker = new InvalidationTracker();
            var e = new Element("div");
            tracker.MarkDirty(e, InvalidationKind.Paint);
            var h = new DirtyHighlighter();
            h.CaptureFrame(tracker);
            Assert.That(h.Active[e].Kind, Is.EqualTo(DirtyHighlightKind.Paint));
        }

        [Test]
        public void DirtyHighlighter_classifies_style_change_as_style() {
            var tracker = new InvalidationTracker();
            var e = new Element("div");
            tracker.MarkDirty(e, InvalidationKind.Style);
            var h = new DirtyHighlighter();
            h.CaptureFrame(tracker);
            Assert.That(h.Active[e].Kind, Is.EqualTo(DirtyHighlightKind.Style));
        }

        [Test]
        public void DirtyHighlighter_decays_over_FlashFrames() {
            var tracker = new InvalidationTracker();
            var e = new Element("div");
            tracker.MarkDirty(e, InvalidationKind.Layout);
            var h = new DirtyHighlighter { FlashFrames = 3 };
            h.CaptureFrame(tracker);
            Assert.That(h.Active[e].FramesRemaining, Is.EqualTo(3));
            tracker.Clear();
            h.CaptureFrame(tracker);
            Assert.That(h.Active[e].FramesRemaining, Is.EqualTo(2));
            h.CaptureFrame(tracker);
            Assert.That(h.Active[e].FramesRemaining, Is.EqualTo(1));
            h.CaptureFrame(tracker);
            Assert.That(h.Active.ContainsKey(e), Is.False);
        }

        [Test]
        public void DirtyHighlighter_refresh_resets_frame_counter() {
            var tracker = new InvalidationTracker();
            var e = new Element("div");
            tracker.MarkDirty(e, InvalidationKind.Style);
            var h = new DirtyHighlighter { FlashFrames = 3 };
            h.CaptureFrame(tracker);
            tracker.Clear();
            h.CaptureFrame(tracker);
            Assert.That(h.Active[e].FramesRemaining, Is.EqualTo(2));
            tracker.MarkDirty(e, InvalidationKind.Style);
            h.CaptureFrame(tracker);
            Assert.That(h.Active[e].FramesRemaining, Is.EqualTo(3));
        }

        [Test]
        public void HoverInspector_resolves_hit_via_hit_tester() {
            var element = new Element("button");
            element.SetAttribute("class", "btn-primary");
            element.SetAttribute("id", "start");
            var hit = new StubHitTester(element);
            var inspector = new HoverInspector();
            var resolved = inspector.Resolve(hit, 100, 50, _ => null);
            Assert.That(resolved, Is.SameAs(element));
            Assert.That(inspector.CurrentElement, Is.SameAs(element));
        }

        [Test]
        public void HoverInspector_resolves_box_via_lookup() {
            var element = new Element("div");
            var box = MakeBox(0, 0, 100, 100);
            box.Element = element;
            var hit = new StubHitTester(element);
            var inspector = new HoverInspector();
            inspector.Resolve(hit, 0, 0, e => e == element ? box : null);
            Assert.That(inspector.CurrentBox, Is.SameAs(box));
        }

        [Test]
        public void HoverInspector_format_includes_tag_and_classes_and_id() {
            var element = new Element("button");
            element.SetAttribute("class", "btn primary");
            element.SetAttribute("id", "start");
            var inspector = new HoverInspector();
            var text = inspector.Format(element, null, null);
            Assert.That(text, Does.Contain("<button"));
            Assert.That(text, Does.Contain("#start"));
            Assert.That(text, Does.Contain(".btn"));
            Assert.That(text, Does.Contain(".primary"));
        }

        [Test]
        public void HoverInspector_format_includes_box_size_when_available() {
            var element = new Element("div");
            var box = MakeBox(10, 20, 80, 40);
            var inspector = new HoverInspector();
            var text = inspector.Format(element, box, null);
            Assert.That(text, Does.Contain("80x40"));
            Assert.That(text, Does.Contain("10,20"));
        }

        [Test]
        public void HoverInspector_format_includes_interesting_style_props() {
            var element = new Element("div");
            var style = new ComputedStyle(element);
            style.Set("display", "flex");
            style.Set("color", "red");
            style.Set("font-size", "14px");
            var inspector = new HoverInspector();
            var text = inspector.Format(element, null, style);
            Assert.That(text, Does.Contain("display: flex"));
            Assert.That(text, Does.Contain("color: red"));
            Assert.That(text, Does.Contain("font-size: 14px"));
        }

        [Test]
        public void CacheStats_records_per_frame_deltas() {
            var converter = new BoxToPaintConverter();
            var stats = new CacheStats();
            // Two boxes: cold pass = 2 misses.
            var s = new ComputedStyle(new Element("div"));
            s.Set("background-color", "red");
            var b1 = MakeBox(0, 0, 10, 10); b1.Style = s;
            var b2 = MakeBox(20, 0, 10, 10); b2.Style = s;
            converter.Convert(b1);
            converter.Convert(b2);
            stats.RecordFrame(converter);
            Assert.That(stats.ThisFrameMisses, Is.GreaterThanOrEqualTo(2));
            // Re-running converts the same two boxes — should be all hits.
            converter.Convert(b1);
            converter.Convert(b2);
            stats.RecordFrame(converter);
            Assert.That(stats.ThisFrameHits, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void CacheStats_hit_ratio_starts_zero() {
            var stats = new CacheStats();
            Assert.That(stats.HitRatio, Is.EqualTo(0));
        }

        [Test]
        public void CacheStats_hit_ratio_reflects_total_history() {
            var converter = new BoxToPaintConverter();
            var stats = new CacheStats();
            var s = new ComputedStyle(new Element("div"));
            s.Set("background-color", "red");
            var box = MakeBox(0, 0, 10, 10); box.Style = s;
            // Frame 1: 1 miss
            converter.Convert(box);
            stats.RecordFrame(converter);
            // Frame 2: 1 hit
            converter.Convert(box);
            stats.RecordFrame(converter);
            Assert.That(stats.TotalHits, Is.EqualTo(1));
            Assert.That(stats.TotalMisses, Is.EqualTo(1));
            Assert.That(stats.HitRatio, Is.EqualTo(0.5).Within(1e-6));
        }

        [Test]
        public void CacheStats_format_contains_hit_and_miss_counts() {
            var stats = new CacheStats();
            var s = stats.Format();
            Assert.That(s, Does.Contain("paint cache"));
            Assert.That(s, Does.Contain("hit"));
            Assert.That(s, Does.Contain("miss"));
        }

        [Test]
        public void PerfReadout_format_emits_required_lines() {
            var p = new PerfReadout();
            p.Start();
            p.RecordFrame(0.016);
            var text = p.Format();
            Assert.That(text, Does.Contain("FPS"));
            Assert.That(text, Does.Contain("frame"));
            Assert.That(text, Does.Contain("cascade"));
            Assert.That(text, Does.Contain("layout"));
            Assert.That(text, Does.Contain("paint"));
            Assert.That(text, Does.Contain("alloc"));
            p.Dispose();
        }

        [Test]
        public void PerfReadout_records_sample_count() {
            var p = new PerfReadout();
            p.Start();
            p.RecordFrame(0.016);
            p.RecordFrame(0.016);
            Assert.That(p.SampleCount, Is.EqualTo(2));
            p.Dispose();
        }

        [Test]
        public void PerfReadout_phase_records_smooth_into_average() {
            var p = new PerfReadout { SmoothingFrames = 1 };
            p.Start();
            p.RecordFrame(0.016);
            p.RecordPhaseMs(0, 5.0);
            p.RecordPhaseMs(1, 7.0);
            p.RecordPhaseMs(2, 3.0);
            // SmoothingFrames=1 + sampleCount=1 -> weight=1.0 so the value
            // overwrites the previous average.
            Assert.That(p.CascadeMs, Is.EqualTo(5.0).Within(1e-6));
            Assert.That(p.LayoutMs, Is.EqualTo(7.0).Within(1e-6));
            Assert.That(p.PaintMs, Is.EqualTo(3.0).Within(1e-6));
            p.Dispose();
        }

        [Test]
        public void PerfReadout_reset_zeroes_averages() {
            var p = new PerfReadout();
            p.Start();
            p.RecordFrame(0.016);
            p.RecordPhaseMs(0, 5.0);
            p.Reset();
            Assert.That(p.CascadeMs, Is.EqualTo(0));
            Assert.That(p.SampleCount, Is.EqualTo(0));
            p.Dispose();
        }

        sealed class StubHitTester : IHitTester {
            readonly Element resolved;
            public StubHitTester(Element resolved) { this.resolved = resolved; }
            public Element HitTest(double x, double y) => resolved;
        }
    }
}
