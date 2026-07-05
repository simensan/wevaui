using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class CssPropertiesTests {
        [Test]
        public void Registry_contains_display() {
            Assert.That(CssProperties.TryGet("display", out var p), Is.True);
            Assert.That(p.Name, Is.EqualTo("display"));
        }

        [Test]
        public void Registry_contains_color_font_and_layout_properties() {
            Assert.That(CssProperties.TryGet("color", out _), Is.True);
            Assert.That(CssProperties.TryGet("font-size", out _), Is.True);
            Assert.That(CssProperties.TryGet("font-family", out _), Is.True);
            Assert.That(CssProperties.TryGet("width", out _), Is.True);
            Assert.That(CssProperties.TryGet("height", out _), Is.True);
            Assert.That(CssProperties.TryGet("padding", out _), Is.True);
            Assert.That(CssProperties.TryGet("margin", out _), Is.True);
            Assert.That(CssProperties.TryGet("background-color", out _), Is.True);
            Assert.That(CssProperties.TryGet("border", out _), Is.True);
        }

        [Test]
        public void Color_is_inherited() {
            Assert.That(CssProperties.IsInherited("color"), Is.True);
        }

        [Test]
        public void Width_is_not_inherited() {
            Assert.That(CssProperties.IsInherited("width"), Is.False);
        }

        [Test]
        public void Font_family_and_font_size_are_inherited() {
            Assert.That(CssProperties.IsInherited("font-family"), Is.True);
            Assert.That(CssProperties.IsInherited("font-size"), Is.True);
            Assert.That(CssProperties.IsInherited("line-height"), Is.True);
            Assert.That(CssProperties.IsInherited("letter-spacing"), Is.True);
        }

        [Test]
        public void Padding_and_margin_and_border_are_not_inherited() {
            Assert.That(CssProperties.IsInherited("padding"), Is.False);
            Assert.That(CssProperties.IsInherited("margin"), Is.False);
            Assert.That(CssProperties.IsInherited("border"), Is.False);
            Assert.That(CssProperties.IsInherited("background-color"), Is.False);
            Assert.That(CssProperties.IsInherited("display"), Is.False);
            Assert.That(CssProperties.IsInherited("position"), Is.False);
        }

        [Test]
        public void Custom_properties_are_inherited() {
            Assert.That(CssProperties.IsInherited("--theme-color"), Is.True);
            Assert.That(CssProperties.IsInherited("--x"), Is.True);
            Assert.That(CssProperties.IsCustomProperty("--anything"), Is.True);
            Assert.That(CssProperties.IsCustomProperty("color"), Is.False);
        }

        [Test]
        public void Custom_property_initial_value_is_empty_string() {
            var p = CssProperties.Get("--my-var");
            Assert.That(p, Is.Not.Null);
            Assert.That(p.IsInherited, Is.True);
            Assert.That(p.InitialValue, Is.EqualTo(""));
        }

        [Test]
        public void Initial_display_is_inline() {
            Assert.That(CssProperties.InitialValueOf("display"), Is.EqualTo("inline"));
        }

        [Test]
        public void Initial_color_is_black() {
            Assert.That(CssProperties.InitialValueOf("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Initial_font_size_is_16px() {
            Assert.That(CssProperties.InitialValueOf("font-size"), Is.EqualTo("16px"));
        }

        [Test]
        public void Initial_width_is_auto() {
            Assert.That(CssProperties.InitialValueOf("width"), Is.EqualTo("auto"));
        }

        [Test]
        public void Initial_font_family_is_sans_serif() {
            Assert.That(CssProperties.InitialValueOf("font-family"), Is.EqualTo("sans-serif"));
        }

        [Test]
        public void Initial_value_for_unknown_property_is_empty() {
            Assert.That(CssProperties.InitialValueOf("not-a-real-property"), Is.EqualTo(""));
        }

        [Test]
        public void All_returns_full_registry_count() {
            Assert.That(CssProperties.All.Count, Is.GreaterThan(60));
            Assert.That(CssProperties.All.ContainsKey("display"), Is.True);
            Assert.That(CssProperties.All.ContainsKey("color"), Is.True);
        }

        [Test]
        public void Table_properties_are_registered_with_spec_inheritance() {
            Assert.That(CssProperties.TryGet("border-collapse", out _), Is.True);
            Assert.That(CssProperties.TryGet("border-spacing", out _), Is.True);
            Assert.That(CssProperties.TryGet("caption-side", out _), Is.True);
            Assert.That(CssProperties.TryGet("empty-cells", out _), Is.True);
            Assert.That(CssProperties.TryGet("table-layout", out _), Is.True);
            Assert.That(CssProperties.TryGet("vertical-align", out _), Is.True);

            Assert.That(CssProperties.IsInherited("border-collapse"), Is.True);
            Assert.That(CssProperties.IsInherited("border-spacing"), Is.True);
            Assert.That(CssProperties.IsInherited("caption-side"), Is.True);
            Assert.That(CssProperties.IsInherited("empty-cells"), Is.True);
            Assert.That(CssProperties.IsInherited("table-layout"), Is.False);
            Assert.That(CssProperties.IsInherited("vertical-align"), Is.False);
        }

        [Test]
        public void Rendered_visual_properties_are_not_stubs() {
            Assert.That(CssProperties.IsStubProperty("backdrop-filter"), Is.False);
            Assert.That(CssProperties.IsStubProperty("clip-path"), Is.False);
            Assert.That(CssProperties.IsStubProperty("mask"), Is.False);
            Assert.That(CssProperties.IsStubProperty("mask-image"), Is.False);
            Assert.That(CssProperties.IsStubProperty("filter"), Is.False);
        }

        // ---- Logical-axis / international CSS ----

        [Test]
        public void Direction_and_writing_mode_are_registered() {
            Assert.That(CssProperties.TryGet("direction", out _), Is.True);
            Assert.That(CssProperties.TryGet("writing-mode", out _), Is.True);
            Assert.That(CssProperties.TryGet("unicode-bidi", out _), Is.True);
            Assert.That(CssProperties.IsInherited("direction"), Is.True);
            Assert.That(CssProperties.IsInherited("writing-mode"), Is.True);
            Assert.That(CssProperties.IsInherited("unicode-bidi"), Is.False);
        }

        [Test]
        public void Logical_property_longhands_are_registered() {
            Assert.That(CssProperties.TryGet("margin-inline-start", out _), Is.True);
            Assert.That(CssProperties.TryGet("margin-inline-end", out _), Is.True);
            Assert.That(CssProperties.TryGet("padding-block-end", out _), Is.True);
            Assert.That(CssProperties.TryGet("border-inline-color", out _), Is.True);
            Assert.That(CssProperties.TryGet("inset-inline-start", out _), Is.True);
            Assert.That(CssProperties.TryGet("inset-block", out _), Is.True);
            Assert.That(CssProperties.TryGet("inline-size", out _), Is.True);
            Assert.That(CssProperties.TryGet("block-size", out _), Is.True);
        }

        [Test]
        public void Direction_rtl_flips_logical_axis_in_layout() {
            Assert.That(LayoutAffectingProperties.IsLayoutAffecting("direction"), Is.True);
            Assert.That(LayoutAffectingProperties.IsLayoutAffecting("writing-mode"), Is.True);
            Assert.That(LayoutAffectingProperties.IsLayoutAffecting("margin-inline-start"), Is.True);
            Assert.That(LayoutAffectingProperties.IsLayoutAffecting("inset-inline-start"), Is.True);
        }

        // CSS Text L3 §7.3.2 — text-justify: auto | inter-word | inter-character |
        // none; inherited; initial auto. Registration-only; honoring inter-
        // character/none is deferred to InlineLayout.
        [Test]
        public void Text_justify_is_registered_as_inherited_keyword_with_auto_initial() {
            Assert.That(CssProperties.TryGet("text-justify", out _), Is.True);
            Assert.That(CssProperties.GetId("text-justify"), Is.GreaterThanOrEqualTo(0));
            Assert.That(CssProperties.IsInherited("text-justify"), Is.True);
            Assert.That(CssProperties.InitialValueOf("text-justify"), Is.EqualTo("auto"));

            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse("#x { text-align: justify; text-justify: inter-word; }"))
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("text-justify"), Is.EqualTo("inter-word"));

            var doc2 = HtmlParser.Parse("<div id=\"y\"></div>");
            var engine2 = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse("#y { text-align: justify; text-justify: none; }"))
            });
            var cs2 = engine2.Compute(doc2.GetElementById("y"));
            Assert.That(cs2.Get("text-justify"), Is.EqualTo("none"));
        }

        // CSS Text L4 §3 — `white-space` shorthand split into
        // `white-space-collapse` (inherited, initial `collapse`) and
        // `text-wrap` (inherited, initial `wrap`). Registration-only; the
        // WhiteSpace resolver still keys off the legacy `white-space` value
        // and partially honors `text-wrap: nowrap`.
        [Test]
        public void White_space_collapse_and_text_wrap_longhands_are_registered_inherited_with_spec_initials() {
            Assert.That(CssProperties.TryGet("white-space-collapse", out _), Is.True);
            Assert.That(CssProperties.GetId("white-space-collapse"), Is.GreaterThanOrEqualTo(0));
            Assert.That(CssProperties.IsInherited("white-space-collapse"), Is.True);
            Assert.That(CssProperties.InitialValueOf("white-space-collapse"), Is.EqualTo("collapse"));

            Assert.That(CssProperties.TryGet("text-wrap", out _), Is.True);
            Assert.That(CssProperties.GetId("text-wrap"), Is.GreaterThanOrEqualTo(0));
            Assert.That(CssProperties.IsInherited("text-wrap"), Is.True);
            Assert.That(CssProperties.InitialValueOf("text-wrap"), Is.EqualTo("wrap"));

            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse("#x { white-space-collapse: preserve; text-wrap: balance; }"))
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("white-space-collapse"), Is.EqualTo("preserve"));
            Assert.That(cs.Get("text-wrap"), Is.EqualTo("balance"));
        }

        // CSS Overflow L4 §6 — overflow-clip-margin: <length>; non-inherited;
        // initial 0px.
        [Test]
        public void Overflow_clip_margin_is_registered_as_non_inherited_length_with_zero_initial() {
            Assert.That(CssProperties.TryGet("overflow-clip-margin", out _), Is.True);
            Assert.That(CssProperties.GetId("overflow-clip-margin"), Is.GreaterThanOrEqualTo(0));
            Assert.That(CssProperties.IsInherited("overflow-clip-margin"), Is.False);
            Assert.That(CssProperties.InitialValueOf("overflow-clip-margin"), Is.EqualTo("0px"));

            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse("#x { overflow: clip; overflow-clip-margin: 8px; }"))
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("overflow-clip-margin"), Is.EqualTo("8px"));

            var parsed = cs.GetParsed(CssProperties.GetId("overflow-clip-margin")) as CssLength;
            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed.Unit, Is.EqualTo(CssLengthUnit.Px));
            Assert.That(parsed.Value, Is.EqualTo(8).Within(0.0001));
        }

        // CSS Overflow L4 §6 — per-side longhands. Each <length>, non-
        // inherited, initial 0px.
        [Test]
        public void Overflow_clip_margin_side_longhands_are_registered_as_non_inherited_lengths() {
            foreach (var name in new[] {
                "overflow-clip-margin-top",
                "overflow-clip-margin-right",
                "overflow-clip-margin-bottom",
                "overflow-clip-margin-left",
            }) {
                Assert.That(CssProperties.TryGet(name, out _), Is.True, name);
                Assert.That(CssProperties.GetId(name), Is.GreaterThanOrEqualTo(0), name);
                Assert.That(CssProperties.IsInherited(name), Is.False, name);
                Assert.That(CssProperties.InitialValueOf(name), Is.EqualTo("0px"), name);
            }
        }
    }
}
