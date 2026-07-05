using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Filters;

namespace Weva.Tests.Paint.Conversion {
    // BoxToPaintConverter.EmitTextRun must emit shadow DrawText commands
    // BEFORE the regular glyph DrawText so the glyphs paint on top. Multiple
    // shadows reverse-emit so the first listed shadow ends up nearest the
    // glyph (last drawn = on top).
    public class TextShadowPaintTests {
        static ComputedStyle TextStyle(string text = "hello") {
            var s = new ComputedStyle(new Element("p"));
            s.Set("color", "white");
            s.Set("font-size", "16px");
            return s;
        }

        static BlockBox Wrap(TextRun tr) {
            var bb = new BlockBox();
            bb.X = 0; bb.Y = 0; bb.Width = 100; bb.Height = 20;
            bb.Style = new ComputedStyle(new Element("div"));
            bb.AddChild(tr);
            return bb;
        }

        static TextRun MakeRun(string text, ComputedStyle style) {
            var tr = new TextRun(text, style, style.Element, null);
            tr.X = 10; tr.Y = 5; tr.Width = 80; tr.Height = 16;
            return tr;
        }

        [Test]
        public void No_shadow_emits_only_glyph_DrawText() {
            var s = TextStyle();
            var tr = MakeRun("hi", s);
            var root = Wrap(tr);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            int drawTexts = 0;
            foreach (var c in cmds) if (c is DrawTextCommand) drawTexts++;
            Assert.That(drawTexts, Is.EqualTo(1));
        }

        [Test]
        public void Single_shadow_emits_two_DrawTexts_shadow_first() {
            var s = TextStyle();
            s.Set("text-shadow", "1px 1px black");
            var tr = MakeRun("hi", s);
            var root = Wrap(tr);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            int firstDrawText = -1;
            int lastDrawText = -1;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is DrawTextCommand) {
                    if (firstDrawText < 0) firstDrawText = i;
                    lastDrawText = i;
                }
            }
            Assert.That(firstDrawText, Is.GreaterThanOrEqualTo(0));
            Assert.That(lastDrawText, Is.GreaterThan(firstDrawText));

            // The first DrawText is the shadow (offset +1,+1 in absolute
            // coords; tr.X=10, tr.Y=5 → shadow bounds X=11, Y=6). The last
            // DrawText is the glyph itself at X=10, Y=5.
            var shadow = (DrawTextCommand)cmds[firstDrawText];
            var glyph = (DrawTextCommand)cmds[lastDrawText];
            Assert.That(shadow.Bounds.X, Is.EqualTo(11).Within(1e-6));
            Assert.That(shadow.Bounds.Y, Is.EqualTo(6).Within(1e-6));
            Assert.That(glyph.Bounds.X, Is.EqualTo(10).Within(1e-6));
            Assert.That(glyph.Bounds.Y, Is.EqualTo(5).Within(1e-6));
        }

        [Test]
        public void Shadow_inherits_currentColor_when_color_omitted() {
            var s = TextStyle();
            s.Set("color", "red");
            s.Set("text-shadow", "1px 1px");
            var tr = MakeRun("hi", s);
            var root = Wrap(tr);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            DrawTextCommand shadow = null;
            foreach (var c in cmds) {
                if (c is DrawTextCommand dt) { shadow = dt; break; }
            }
            Assert.That(shadow, Is.Not.Null);
            Assert.That(shadow.Color.R, Is.GreaterThan(0.5f));
            Assert.That(shadow.Color.G, Is.LessThan(0.05f));
        }

        [Test]
        public void Multiple_shadows_emit_back_to_front_then_glyph() {
            // CSS spec: first listed shadow ends up on top. So among the
            // shadow DrawTexts, the FIRST listed must be the LAST one drawn
            // before the glyph. Here we list (red, blue): we want emission
            // order [blue-shadow, red-shadow, glyph].
            var s = TextStyle();
            s.Set("text-shadow", "1px 1px red, 2px 2px blue");
            var tr = MakeRun("hi", s);
            var root = Wrap(tr);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            var dts = new System.Collections.Generic.List<DrawTextCommand>();
            foreach (var c in cmds) if (c is DrawTextCommand dt) dts.Add(dt);
            Assert.That(dts.Count, Is.EqualTo(3));

            // dts[0] = bottom shadow (blue, 2px offset)
            // dts[1] = top shadow (red, 1px offset)
            // dts[2] = glyph (white)
            Assert.That(dts[0].Color.B, Is.GreaterThan(0.5f), "first emitted shadow should be the bottom (last listed) — blue");
            Assert.That(dts[0].Bounds.X, Is.EqualTo(12).Within(1e-6)); // 10 + 2
            Assert.That(dts[1].Color.R, Is.GreaterThan(0.5f), "second emitted shadow should be the top (first listed) — red");
            Assert.That(dts[1].Bounds.X, Is.EqualTo(11).Within(1e-6)); // 10 + 1
            Assert.That(dts[2].Bounds.X, Is.EqualTo(10).Within(1e-6)); // glyph at run origin
        }

        [Test]
        public void Negative_offset_shadow_emitted_above_left_of_glyph() {
            var s = TextStyle();
            s.Set("text-shadow", "-1px -1px black");
            var tr = MakeRun("hi", s);
            var root = Wrap(tr);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            DrawTextCommand shadow = null;
            foreach (var c in cmds) {
                if (c is DrawTextCommand dt) { shadow = dt; break; }
            }
            Assert.That(shadow.Bounds.X, Is.EqualTo(9).Within(1e-6));
            Assert.That(shadow.Bounds.Y, Is.EqualTo(4).Within(1e-6));
        }

        [Test]
        public void Blur_radius_wraps_shadow_text_in_expanded_filter_scope() {
            var s = TextStyle();
            s.Set("text-shadow", "2px 2px 4px black");
            var tr = MakeRun("hi", s);
            var root = Wrap(tr);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            int pushFilters = 0;
            PushFilterCommand push = null;
            DrawTextCommand shadow = null;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is PushFilterCommand pf) {
                    pushFilters++;
                    push = pf;
                }
                if (shadow == null && cmds[i] is DrawTextCommand dt) shadow = dt;
            }

            Assert.That(pushFilters, Is.EqualTo(1));
            Assert.That(push, Is.Not.Null);
            Assert.That(push.Filters.Functions.Count, Is.EqualTo(1));
            Assert.That(push.Filters.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(push.Bounds.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(push.Bounds.Y, Is.EqualTo(-5).Within(1e-6));
            Assert.That(push.Bounds.Width, Is.EqualTo(104).Within(1e-6));
            Assert.That(push.Bounds.Height, Is.EqualTo(40).Within(1e-6));

            Assert.That(shadow, Is.Not.Null);
            // Shadow lands at the exact offset.
            Assert.That(shadow.Bounds.X, Is.EqualTo(12).Within(1e-6)); // 10 + 2
            Assert.That(shadow.Bounds.Y, Is.EqualTo(7).Within(1e-6)); //  5 + 2
            // Bounds keep the run's geometry — they are NOT inflated by blur.
            Assert.That(shadow.Bounds.Width, Is.EqualTo(tr.Width).Within(1e-6));
            Assert.That(shadow.Bounds.Height, Is.EqualTo(tr.Height).Within(1e-6));
            Assert.That(shadow.BlurRadius, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void Parent_filter_scope_includes_descendant_blurred_text_shadow_overflow() {
            var parentStyle = new ComputedStyle(new Element("div"));
            parentStyle.Set("filter", "contrast(1.2)");
            var root = new BlockBox {
                X = 0,
                Y = 0,
                Width = 20,
                Height = 20,
                Style = parentStyle
            };

            var textStyle = TextStyle();
            textStyle.Set("text-shadow", "0 0 12px rgba(251, 191, 36, 0.76)");
            var tr = MakeRun("*", textStyle);
            tr.X = 4;
            tr.Y = 4;
            tr.Width = 12;
            tr.Height = 12;
            root.AddChild(tr);

            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            PushFilterCommand outer = null;
            foreach (var c in cmds) {
                if (c is PushFilterCommand pf
                    && pf.Filters.Functions.Count == 1
                    && pf.Filters.Functions[0] is ContrastFilter) {
                    outer = pf;
                    break;
                }
            }

            Assert.That(outer, Is.Not.Null);
            Assert.That(outer.Bounds.X, Is.EqualTo(-20).Within(1e-6));
            Assert.That(outer.Bounds.Y, Is.EqualTo(-20).Within(1e-6));
            Assert.That(outer.Bounds.Right, Is.EqualTo(40).Within(1e-6));
            Assert.That(outer.Bounds.Bottom, Is.EqualTo(40).Within(1e-6));
        }

        [Test]
        public void Sharp_text_shadow_stays_zero_blur() {
            // Pins the "0px blur stays crisp" contract — a `text-shadow: 1px
            // 1px 0 black` (or the two-length form `1px 1px black`) must not
            // report a non-zero BlurRadius or enter the filter path, keeping
            // their cost identical to v0.
            var s = TextStyle();
            s.Set("text-shadow", "1px 1px 0 black");
            var tr = MakeRun("hi", s);
            var root = Wrap(tr);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            DrawTextCommand shadow = null;
            int pushFilters = 0;
            foreach (var c in cmds) {
                if (c is PushFilterCommand) pushFilters++;
                if (shadow == null && c is DrawTextCommand dt) shadow = dt;
            }
            Assert.That(shadow, Is.Not.Null);
            Assert.That(shadow.BlurRadius, Is.EqualTo(0).Within(1e-6));
            Assert.That(pushFilters, Is.EqualTo(0));
        }

        [Test]
        public void Glyph_DrawTextCommand_keeps_zero_blur_radius() {
            // Blurred shadows live in their own filter scope; the glyph itself
            // is always a crisp DrawTextCommand above that filtered result.
            var s = TextStyle();
            s.Set("text-shadow", "0 1px 6px rgba(0,0,0,0.7)");
            var tr = MakeRun("hi", s);
            var root = Wrap(tr);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            DrawTextCommand glyph = null;
            for (int i = cmds.Count - 1; i >= 0; i--) {
                if (cmds[i] is DrawTextCommand dt) { glyph = dt; break; }
            }
            Assert.That(glyph, Is.Not.Null);
            Assert.That(glyph.BlurRadius, Is.EqualTo(0).Within(1e-6),
                "Glyph DrawText must stay crisp even when text-shadow has blur");
        }

        [Test]
        public void Pool_RentDrawText_blur_overload_resets_on_recycle() {
            // The pool's blur-aware Set overload must reset BlurRadius back
            // to 0 when a non-blur RentDrawText is the next user of the
            // recycled instance. Without this, a returned blurred-shadow
            // command would emit a "blurred glyph" the next time the slot
            // was reused — silently corrupting later runs.
            var pool = new PaintCommandPool();
            var font = new FontHandle("system-ui", 16, 400, FontStyle.Normal);
            var first = pool.RentDrawText(new Rect(0, 0, 10, 10), "hi", font, LinearColor.Black, TextDecoration.None, 0, 4);
            Assert.That(first.BlurRadius, Is.EqualTo(4).Within(1e-6));
            var list = new PaintList();
            list.Add(first);
            pool.ReturnAll(list);

            var second = pool.RentDrawText(new Rect(0, 0, 10, 10), "hi", font, LinearColor.Black, TextDecoration.None);
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.BlurRadius, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void None_explicitly_emits_only_glyph() {
            var s = TextStyle();
            s.Set("text-shadow", "none");
            var tr = MakeRun("hi", s);
            var root = Wrap(tr);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            int drawTexts = 0;
            foreach (var c in cmds) if (c is DrawTextCommand) drawTexts++;
            Assert.That(drawTexts, Is.EqualTo(1));
        }

        [Test]
        public void Text_only_brightness_filter_folds_into_DrawText_color() {
            var parentStyle = new ComputedStyle(new Element("span"));
            parentStyle.Set("filter", "brightness(1.25)");
            var root = new BlockBox {
                X = 0,
                Y = 0,
                Width = 100,
                Height = 20,
                Style = parentStyle
            };

            var s = TextStyle();
            s.Set("color", "white");
            var tr = MakeRun("hi", s);
            root.AddChild(tr);

            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            int pushFilters = 0;
            DrawTextCommand glyph = null;
            foreach (var c in cmds) {
                if (c is PushFilterCommand) pushFilters++;
                if (c is DrawTextCommand dt) glyph = dt;
            }

            Assert.That(pushFilters, Is.EqualTo(0));
            Assert.That(glyph, Is.Not.Null);
            Assert.That(glyph.Color.R, Is.EqualTo(1.25f).Within(1e-6));
            Assert.That(glyph.Color.G, Is.EqualTo(1.25f).Within(1e-6));
            Assert.That(glyph.Color.B, Is.EqualTo(1.25f).Within(1e-6));
            Assert.That(glyph.Color.A, Is.EqualTo(1f).Within(1e-6));
        }

        [Test]
        public void Text_shadow_brightness_filter_folds_into_shadow_and_glyph_colors() {
            var parentStyle = new ComputedStyle(new Element("span"));
            parentStyle.Set("filter", "brightness(1.25)");
            var root = new BlockBox {
                X = 0,
                Y = 0,
                Width = 100,
                Height = 20,
                Style = parentStyle
            };

            var s = TextStyle();
            s.Set("color", "white");
            s.Set("text-shadow", "0 0 8px white");
            var tr = MakeRun("hi", s);
            root.AddChild(tr);

            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            int pushFilters = 0;
            int brightenedText = 0;
            foreach (var c in cmds) {
                if (c is PushFilterCommand) pushFilters++;
                if (c is DrawTextCommand dt && dt.Color.R > 1.24f) brightenedText++;
            }

            Assert.That(pushFilters, Is.EqualTo(1),
                "brightness folds into text colors, but blurred text-shadow still needs one blur filter scope");
            Assert.That(brightenedText, Is.EqualTo(2), "both the shadow phantom and glyph should be color-folded");
        }

        [Test]
        public void Brightness_filter_with_color_decoration_folds_without_filter_scope() {
            var parentStyle = new ComputedStyle(new Element("span"));
            parentStyle.Set("filter", "brightness(1.25)");
            parentStyle.Set("background-color", "red");
            var root = new BlockBox {
                X = 0,
                Y = 0,
                Width = 100,
                Height = 20,
                Style = parentStyle
            };

            var s = TextStyle();
            var tr = MakeRun("hi", s);
            root.AddChild(tr);

            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            int pushFilters = 0;
            FillRectCommand fill = null;
            foreach (var c in cmds) if (c is PushFilterCommand) pushFilters++;
            foreach (var c in cmds) if (c is FillRectCommand fr) fill = fr;

            Assert.That(pushFilters, Is.EqualTo(0));
            Assert.That(fill, Is.Not.Null);
            Assert.That(fill.Brush.Color.R, Is.EqualTo(1.25f).Within(1e-6));
        }
    }
}
