using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Parsing;

namespace Weva.Tests.Conformance {
    /// <summary>
    /// These tests pin the surface documented in /CONFORMANCE.md to the actual
    /// runtime behavior. If a property listed in the reference is removed from
    /// the registry, or a selector listed is dropped from the parser, these
    /// tests fail and the reference must be updated to match.
    /// </summary>
    public class ConformanceReferenceTests {
        static void AssertPropertyParses(string property, string value) {
            string css = "el { " + property + ": " + value + "; }";
            var sheet = CssParser.Parse(css, new ParseOptions { ThrowOnError = true });
            Assert.That(sheet.Rules, Has.Count.EqualTo(1),
                "Expected one parsed rule for `" + css + "`");
        }

        static void AssertPropertyRegistered(string property) {
            Assert.That(CssProperties.TryGet(property, out _), Is.True,
                "Property `" + property + "` must appear in the cascade registry.");
        }

        static void AssertSelectorParses(string selector) {
            Assert.DoesNotThrow(() => SelectorParser.Parse(selector),
                "Selector `" + selector + "` must parse without error.");
        }

        // --- Layout ----------------------------------------------------------

        [Test] public void Property_display() { AssertPropertyRegistered("display"); AssertPropertyParses("display", "flex"); }
        [Test] public void Property_position() { AssertPropertyRegistered("position"); AssertPropertyParses("position", "absolute"); }
        [Test] public void Property_top() { AssertPropertyRegistered("top"); AssertPropertyParses("top", "0"); }
        [Test] public void Property_right() { AssertPropertyRegistered("right"); AssertPropertyParses("right", "0"); }
        [Test] public void Property_bottom() { AssertPropertyRegistered("bottom"); AssertPropertyParses("bottom", "0"); }
        [Test] public void Property_left() { AssertPropertyRegistered("left"); AssertPropertyParses("left", "0"); }
        [Test] public void Property_inset_inline_start() { AssertPropertyRegistered("inset-inline-start"); AssertPropertyParses("inset-inline-start", "1rem"); }
        [Test] public void Property_direction() { AssertPropertyRegistered("direction"); AssertPropertyParses("direction", "rtl"); }
        [Test] public void Property_writing_mode() { AssertPropertyRegistered("writing-mode"); AssertPropertyParses("writing-mode", "vertical-rl"); }
        [Test] public void Property_z_index() { AssertPropertyRegistered("z-index"); AssertPropertyParses("z-index", "10"); }
        [Test] public void Property_overflow() { AssertPropertyRegistered("overflow"); AssertPropertyParses("overflow", "hidden"); }
        [Test] public void Property_overflow_x() { AssertPropertyRegistered("overflow-x"); AssertPropertyParses("overflow-x", "scroll"); }
        [Test] public void Property_overflow_y() { AssertPropertyRegistered("overflow-y"); AssertPropertyParses("overflow-y", "auto"); }

        // --- Flex ------------------------------------------------------------

        [Test] public void Property_flex() { AssertPropertyRegistered("flex"); AssertPropertyParses("flex", "1 1 auto"); }
        [Test] public void Property_flex_direction() { AssertPropertyRegistered("flex-direction"); AssertPropertyParses("flex-direction", "column"); }
        [Test] public void Property_flex_wrap() { AssertPropertyRegistered("flex-wrap"); AssertPropertyParses("flex-wrap", "wrap"); }
        [Test] public void Property_flex_basis() { AssertPropertyRegistered("flex-basis"); AssertPropertyParses("flex-basis", "auto"); }
        [Test] public void Property_flex_grow() { AssertPropertyRegistered("flex-grow"); AssertPropertyParses("flex-grow", "1"); }
        [Test] public void Property_flex_shrink() { AssertPropertyRegistered("flex-shrink"); AssertPropertyParses("flex-shrink", "0"); }
        [Test] public void Property_justify_content() { AssertPropertyRegistered("justify-content"); AssertPropertyParses("justify-content", "space-between"); }
        [Test] public void Property_align_items() { AssertPropertyRegistered("align-items"); AssertPropertyParses("align-items", "center"); }
        [Test] public void Property_align_self() { AssertPropertyRegistered("align-self"); AssertPropertyParses("align-self", "flex-end"); }
        [Test] public void Property_align_content() { AssertPropertyRegistered("align-content"); AssertPropertyParses("align-content", "stretch"); }
        [Test] public void Property_gap() { AssertPropertyRegistered("gap"); AssertPropertyParses("gap", "16px"); }
        [Test] public void Property_row_gap() { AssertPropertyRegistered("row-gap"); AssertPropertyParses("row-gap", "8px"); }
        [Test] public void Property_column_gap() { AssertPropertyRegistered("column-gap"); AssertPropertyParses("column-gap", "8px"); }
        [Test] public void Property_order() { AssertPropertyRegistered("order"); AssertPropertyParses("order", "2"); }

        // --- Grid ------------------------------------------------------------

        [Test] public void Property_grid_template_columns() { AssertPropertyRegistered("grid-template-columns"); AssertPropertyParses("grid-template-columns", "repeat(3, 1fr)"); }
        [Test] public void Property_grid_template_rows() { AssertPropertyRegistered("grid-template-rows"); AssertPropertyParses("grid-template-rows", "auto 1fr"); }
        [Test] public void Property_grid_template_areas() { AssertPropertyRegistered("grid-template-areas"); AssertPropertyParses("grid-template-areas", "\"head head\" \"body side\""); }
        [Test] public void Property_grid_column() { AssertPropertyRegistered("grid-column"); AssertPropertyParses("grid-column", "1 / span 2"); }
        [Test] public void Property_grid_row() { AssertPropertyRegistered("grid-row"); AssertPropertyParses("grid-row", "2 / 4"); }
        [Test] public void Property_grid_auto_flow() { AssertPropertyRegistered("grid-auto-flow"); AssertPropertyParses("grid-auto-flow", "row dense"); }
        [Test] public void Property_grid_auto_columns() { AssertPropertyRegistered("grid-auto-columns"); AssertPropertyParses("grid-auto-columns", "minmax(100px, 1fr)"); }
        [Test] public void Property_grid_auto_rows() { AssertPropertyRegistered("grid-auto-rows"); AssertPropertyParses("grid-auto-rows", "auto"); }
        [Test] public void Property_place_items() { AssertPropertyRegistered("place-items"); AssertPropertyParses("place-items", "center stretch"); }
        [Test] public void Property_place_content() { AssertPropertyRegistered("place-content"); AssertPropertyParses("place-content", "center"); }

        // --- Tables ----------------------------------------------------------

        [Test] public void Property_border_collapse() { AssertPropertyRegistered("border-collapse"); AssertPropertyParses("border-collapse", "separate"); }
        [Test] public void Property_border_spacing() { AssertPropertyRegistered("border-spacing"); AssertPropertyParses("border-spacing", "2px 4px"); }
        [Test] public void Property_caption_side() { AssertPropertyRegistered("caption-side"); AssertPropertyParses("caption-side", "top"); }
        [Test] public void Property_empty_cells() { AssertPropertyRegistered("empty-cells"); AssertPropertyParses("empty-cells", "show"); }
        [Test] public void Property_table_layout() { AssertPropertyRegistered("table-layout"); AssertPropertyParses("table-layout", "auto"); }
        [Test] public void Property_vertical_align() { AssertPropertyRegistered("vertical-align"); AssertPropertyParses("vertical-align", "middle"); }

        // --- Box model -------------------------------------------------------

        [Test] public void Property_width() { AssertPropertyRegistered("width"); AssertPropertyParses("width", "100%"); }
        [Test] public void Property_inline_size() { AssertPropertyRegistered("inline-size"); AssertPropertyParses("inline-size", "20ch"); }
        [Test] public void Property_height() { AssertPropertyRegistered("height"); AssertPropertyParses("height", "auto"); }
        [Test] public void Property_block_size() { AssertPropertyRegistered("block-size"); AssertPropertyParses("block-size", "120px"); }
        [Test] public void Property_min_width() { AssertPropertyRegistered("min-width"); AssertPropertyParses("min-width", "0"); }
        [Test] public void Property_min_height() { AssertPropertyRegistered("min-height"); AssertPropertyParses("min-height", "100px"); }
        [Test] public void Property_max_width() { AssertPropertyRegistered("max-width"); AssertPropertyParses("max-width", "640px"); }
        [Test] public void Property_max_height() { AssertPropertyRegistered("max-height"); AssertPropertyParses("max-height", "none"); }
        [Test] public void Property_padding() { AssertPropertyRegistered("padding"); AssertPropertyParses("padding", "8px 16px"); }
        [Test] public void Property_padding_top() { AssertPropertyRegistered("padding-top"); AssertPropertyParses("padding-top", "4px"); }
        [Test] public void Property_padding_inline() { AssertPropertyRegistered("padding-inline"); AssertPropertyParses("padding-inline", "4px 8px"); }
        [Test] public void Property_margin() { AssertPropertyRegistered("margin"); AssertPropertyParses("margin", "0 auto"); }
        [Test] public void Property_margin_left() { AssertPropertyRegistered("margin-left"); AssertPropertyParses("margin-left", "auto"); }
        [Test] public void Property_margin_inline_start() { AssertPropertyRegistered("margin-inline-start"); AssertPropertyParses("margin-inline-start", "auto"); }
        [Test] public void Property_border_top_width() { AssertPropertyRegistered("border-top-width"); AssertPropertyParses("border-top-width", "1px"); }
        [Test] public void Property_border_inline_start() { AssertPropertyRegistered("border-inline-start"); AssertPropertyParses("border-inline-start", "1px solid red"); }
        [Test] public void Property_border_right_style() { AssertPropertyRegistered("border-right-style"); AssertPropertyParses("border-right-style", "dashed"); }
        [Test] public void Property_border_bottom_color() { AssertPropertyRegistered("border-bottom-color"); AssertPropertyParses("border-bottom-color", "red"); }
        [Test] public void Property_border_radius() { AssertPropertyRegistered("border-radius"); AssertPropertyParses("border-radius", "8px"); }
        [Test] public void Property_box_sizing_default_is_content_box() {
            // CSS Basic User Interface §4.1: the initial value of `box-sizing`
            // is `content-box`. Earlier versions of this engine defaulted to
            // `border-box` as a deliberate spec break; restored to spec to
            // match Chrome's getBoundingClientRect output in LayoutDiffTests.
            Assert.That(CssProperties.InitialValueOf("box-sizing"), Is.EqualTo("content-box"));
        }

        // --- Typography ------------------------------------------------------

        [Test] public void Property_color() { AssertPropertyRegistered("color"); AssertPropertyParses("color", "#4f46e5"); }
        [Test] public void Property_font_family() { AssertPropertyRegistered("font-family"); AssertPropertyParses("font-family", "\"Helvetica\", sans-serif"); }
        [Test] public void Property_font_size() { AssertPropertyRegistered("font-size"); AssertPropertyParses("font-size", "16px"); }
        [Test] public void Property_font_weight() { AssertPropertyRegistered("font-weight"); AssertPropertyParses("font-weight", "700"); }
        [Test] public void Property_font_style() { AssertPropertyRegistered("font-style"); AssertPropertyParses("font-style", "italic"); }
        [Test] public void Property_font_variant() { AssertPropertyRegistered("font-variant"); AssertPropertyParses("font-variant", "small-caps"); }
        [Test] public void Property_line_height() { AssertPropertyRegistered("line-height"); AssertPropertyParses("line-height", "1.5"); }
        [Test] public void Property_letter_spacing() { AssertPropertyRegistered("letter-spacing"); AssertPropertyParses("letter-spacing", "0.05em"); }
        [Test] public void Property_text_align() { AssertPropertyRegistered("text-align"); AssertPropertyParses("text-align", "center"); }
        [Test] public void Property_text_align_last() { AssertPropertyRegistered("text-align-last"); AssertPropertyParses("text-align-last", "end"); }
        [Test] public void Property_text_indent() { AssertPropertyRegistered("text-indent"); AssertPropertyParses("text-indent", "2em"); }
        [Test] public void Property_text_wrap() { AssertPropertyRegistered("text-wrap"); AssertPropertyParses("text-wrap", "nowrap"); }
        [Test] public void Property_text_transform() { AssertPropertyRegistered("text-transform"); AssertPropertyParses("text-transform", "uppercase"); }
        [Test] public void Property_text_decoration() { AssertPropertyRegistered("text-decoration"); AssertPropertyParses("text-decoration", "underline"); }
        [Test] public void Property_text_overflow() { AssertPropertyRegistered("text-overflow"); AssertPropertyParses("text-overflow", "ellipsis"); }
        [Test] public void Property_white_space() { AssertPropertyRegistered("white-space"); AssertPropertyParses("white-space", "pre-wrap"); }
        [Test] public void Property_tab_size() { AssertPropertyRegistered("tab-size"); AssertPropertyParses("tab-size", "4"); }
        [Test] public void Property_hyphens() { AssertPropertyRegistered("hyphens"); AssertPropertyParses("hyphens", "manual"); }

        // --- Backgrounds -----------------------------------------------------

        [Test] public void Property_background_color() { AssertPropertyRegistered("background-color"); AssertPropertyParses("background-color", "rgba(0, 0, 0, 0.5)"); }
        [Test] public void Property_background_image_url() { AssertPropertyRegistered("background-image"); AssertPropertyParses("background-image", "url(\"logo.png\")"); }
        [Test] public void Property_background_image_linear_gradient() { AssertPropertyParses("background-image", "linear-gradient(90deg, red, blue)"); }
        [Test] public void Property_background_image_radial_gradient() { AssertPropertyParses("background-image", "radial-gradient(circle, red, transparent)"); }
        [Test] public void Property_background_size() { AssertPropertyRegistered("background-size"); AssertPropertyParses("background-size", "cover"); }
        [Test] public void Property_background_position() { AssertPropertyRegistered("background-position"); AssertPropertyParses("background-position", "center top"); }
        [Test] public void Property_background_repeat() { AssertPropertyRegistered("background-repeat"); AssertPropertyParses("background-repeat", "no-repeat"); }
        [Test] public void Property_background_clip() { AssertPropertyRegistered("background-clip"); AssertPropertyParses("background-clip", "padding-box"); }

        // --- Effects ---------------------------------------------------------

        [Test] public void Property_opacity() { AssertPropertyRegistered("opacity"); AssertPropertyParses("opacity", "0.5"); }
        [Test] public void Property_visibility() { AssertPropertyRegistered("visibility"); AssertPropertyParses("visibility", "hidden"); }
        [Test] public void Property_box_shadow() { AssertPropertyRegistered("box-shadow"); AssertPropertyParses("box-shadow", "0 2px 4px rgba(0, 0, 0, 0.3)"); }
        [Test] public void Property_transform_translate() { AssertPropertyRegistered("transform"); AssertPropertyParses("transform", "translate(8px, -4px)"); }
        [Test] public void Property_transform_scale() { AssertPropertyParses("transform", "scale(0.97)"); }
        [Test] public void Property_transform_rotate() { AssertPropertyParses("transform", "rotate(45deg)"); }
        [Test] public void Property_transform_origin() { AssertPropertyRegistered("transform-origin"); AssertPropertyParses("transform-origin", "50% 50%"); }
        [Test] public void Property_filter_blur() { AssertPropertyRegistered("filter"); AssertPropertyParses("filter", "blur(4px)"); }
        [Test] public void Property_filter_brightness() { AssertPropertyParses("filter", "brightness(1.2)"); }
        [Test] public void Property_filter_drop_shadow() { AssertPropertyParses("filter", "drop-shadow(0 2px 4px rgba(0,0,0,0.3))"); }

        // --- Animation -------------------------------------------------------

        [Test] public void Property_transition() { AssertPropertyRegistered("transition"); AssertPropertyParses("transition", "transform 200ms ease"); }
        [Test] public void Property_transition_property() { AssertPropertyRegistered("transition-property"); AssertPropertyParses("transition-property", "all"); }
        [Test] public void Property_transition_duration() { AssertPropertyRegistered("transition-duration"); AssertPropertyParses("transition-duration", "200ms"); }
        [Test] public void Property_transition_timing_function() { AssertPropertyRegistered("transition-timing-function"); AssertPropertyParses("transition-timing-function", "cubic-bezier(0.25, 0.1, 0.25, 1)"); }
        [Test] public void Property_transition_delay() { AssertPropertyRegistered("transition-delay"); AssertPropertyParses("transition-delay", "0s"); }
        [Test] public void Property_animation() { AssertPropertyRegistered("animation"); AssertPropertyParses("animation", "fade-in 300ms ease 0s 1 normal"); }
        [Test] public void Property_animation_iteration_count() { AssertPropertyRegistered("animation-iteration-count"); AssertPropertyParses("animation-iteration-count", "infinite"); }
        [Test] public void Property_animation_direction() { AssertPropertyRegistered("animation-direction"); AssertPropertyParses("animation-direction", "alternate"); }
        [Test] public void Property_animation_fill_mode() { AssertPropertyRegistered("animation-fill-mode"); AssertPropertyParses("animation-fill-mode", "forwards"); }

        // --- Custom properties / wide keywords -------------------------------

        [Test] public void Custom_property_declaration_parses() { AssertPropertyParses("--accent", "#4f46e5"); }
        [Test] public void Var_reference_in_value_parses() { AssertPropertyParses("color", "var(--accent, blue)"); }
        [Test] public void Calc_in_value_parses() { AssertPropertyParses("width", "calc(100% - 32px)"); }
        [Test] public void Min_in_value_parses() { AssertPropertyParses("width", "min(100%, 600px)"); }
        [Test] public void Max_in_value_parses() { AssertPropertyParses("width", "max(50px, 5vw)"); }
        [Test] public void Clamp_in_value_parses() { AssertPropertyParses("font-size", "clamp(12px, 2vw, 24px)"); }
        [Test] public void Wide_keyword_inherit_parses() { AssertPropertyParses("color", "inherit"); }
        [Test] public void Wide_keyword_initial_parses() { AssertPropertyParses("color", "initial"); }
        [Test] public void Wide_keyword_unset_parses() { AssertPropertyParses("color", "unset"); }

        // --- Selectors (simple / combinator / pseudo) ------------------------

        [Test] public void Selector_universal() { AssertSelectorParses("*"); }
        [Test] public void Selector_type() { AssertSelectorParses("button"); }
        [Test] public void Selector_class() { AssertSelectorParses(".btn"); }
        [Test] public void Selector_id() { AssertSelectorParses("#main"); }
        [Test] public void Selector_attr_exists() { AssertSelectorParses("[disabled]"); }
        [Test] public void Selector_attr_equals() { AssertSelectorParses("[type=text]"); }
        [Test] public void Selector_attr_includes() { AssertSelectorParses("[class~=primary]"); }
        [Test] public void Selector_attr_dashmatch() { AssertSelectorParses("[lang|=en]"); }
        [Test] public void Selector_attr_prefix() { AssertSelectorParses("[href^=https]"); }
        [Test] public void Selector_attr_suffix() { AssertSelectorParses("[src$=.png]"); }
        [Test] public void Selector_attr_substring() { AssertSelectorParses("[href*=example]"); }
        [Test] public void Selector_descendant() { AssertSelectorParses("nav a"); }
        [Test] public void Selector_child() { AssertSelectorParses("ul > li"); }
        [Test] public void Selector_adjacent_sibling() { AssertSelectorParses("h2 + p"); }
        [Test] public void Selector_general_sibling() { AssertSelectorParses("h2 ~ p"); }
        [Test] public void Selector_first_child() { AssertSelectorParses("li:first-child"); }
        [Test] public void Selector_last_child() { AssertSelectorParses("li:last-child"); }
        [Test] public void Selector_only_child() { AssertSelectorParses("li:only-child"); }
        [Test] public void Selector_nth_child() { AssertSelectorParses("li:nth-child(2n+1)"); }
        [Test] public void Selector_nth_child_odd() { AssertSelectorParses("li:nth-child(odd)"); }
        [Test] public void Selector_nth_last_child() { AssertSelectorParses("li:nth-last-child(2)"); }
        [Test] public void Selector_nth_of_type() { AssertSelectorParses("p:nth-of-type(3)"); }
        [Test] public void Selector_nth_last_of_type() { AssertSelectorParses("p:nth-last-of-type(1)"); }
        [Test] public void Selector_empty() { AssertSelectorParses("div:empty"); }
        [Test] public void Selector_not() { AssertSelectorParses("button:not(.primary)"); }
        [Test] public void Selector_is() { AssertSelectorParses(":is(h1, h2, h3)"); }
        [Test] public void Selector_where() { AssertSelectorParses(":where(.a, .b)"); }
        [Test] public void Selector_has() { AssertSelectorParses("section:has(> .selected)"); }
        [Test] public void Selector_lang() { AssertSelectorParses(":lang(en-US)"); }
        [Test] public void Selector_dir() { AssertSelectorParses(":dir(rtl)"); }
        [Test] public void Selector_root() { AssertSelectorParses(":root"); }
        [Test] public void Selector_link() { AssertSelectorParses("a:link"); }
        [Test] public void Selector_visited() { AssertSelectorParses("a:visited"); }
        [Test] public void Selector_any_link() { AssertSelectorParses(":any-link"); }
        [Test] public void Selector_target() { AssertSelectorParses(":target"); }
        [Test] public void Selector_scope() { AssertSelectorParses(":scope"); }
        [Test] public void Selector_hover() { AssertSelectorParses("a:hover"); }
        [Test] public void Selector_focus() { AssertSelectorParses("input:focus"); }
        [Test] public void Selector_focus_visible() { AssertSelectorParses("button:focus-visible"); }
        [Test] public void Selector_focus_within() { AssertSelectorParses("form:focus-within"); }
        [Test] public void Selector_active() { AssertSelectorParses("button:active"); }
        [Test] public void Selector_disabled() { AssertSelectorParses("input:disabled"); }
        [Test] public void Selector_checked() { AssertSelectorParses("input:checked"); }
        [Test] public void Selector_in_range() { AssertSelectorParses("input:in-range"); }
        [Test] public void Selector_out_of_range() { AssertSelectorParses("input:out-of-range"); }
        [Test] public void Selector_user_valid() { AssertSelectorParses("input:user-valid"); }
        [Test] public void Selector_user_invalid() { AssertSelectorParses("input:user-invalid"); }
        [Test] public void Selector_default() { AssertSelectorParses("button:default"); }
        [Test] public void Selector_placeholder_shown() { AssertSelectorParses("input:placeholder-shown"); }
        [Test] public void Selector_pseudo_before() { AssertSelectorParses("p::before"); }
        [Test] public void Selector_pseudo_after() { AssertSelectorParses("p::after"); }
        [Test] public void Selector_legacy_pseudo_before() { AssertSelectorParses("p:before"); }
        [Test] public void Selector_legacy_pseudo_after() { AssertSelectorParses("p:after"); }
        [Test] public void Selector_pseudo_placeholder() { AssertSelectorParses("input::placeholder"); }
        [Test] public void Selector_pseudo_selection() { AssertSelectorParses("::selection"); }
        [Test] public void Selector_pseudo_backdrop() { AssertSelectorParses("dialog::backdrop"); }

        // --- Length units ----------------------------------------------------

        [Test] public void Length_unit_px() { AssertPropertyParses("width", "16px"); }
        [Test] public void Length_unit_em() { AssertPropertyParses("font-size", "1.5em"); }
        [Test] public void Length_unit_rem() { AssertPropertyParses("font-size", "1rem"); }
        [Test] public void Length_unit_percent() { AssertPropertyParses("width", "50%"); }
        [Test] public void Length_unit_vh() { AssertPropertyParses("height", "100vh"); }
        [Test] public void Length_unit_vw() { AssertPropertyParses("width", "50vw"); }
        [Test] public void Length_unit_vmin() { AssertPropertyParses("width", "50vmin"); }
        [Test] public void Length_unit_vmax() { AssertPropertyParses("height", "50vmax"); }
        [Test] public void Length_unit_pt() { AssertPropertyParses("font-size", "12pt"); }

        // --- Color formats ---------------------------------------------------

        [Test] public void Color_hex_short() { AssertPropertyParses("color", "#fff"); }
        [Test] public void Color_hex_long() { AssertPropertyParses("color", "#4f46e5"); }
        [Test] public void Color_rgb() { AssertPropertyParses("color", "rgb(79, 70, 229)"); }
        [Test] public void Color_rgba() { AssertPropertyParses("color", "rgba(79, 70, 229, 0.5)"); }
        [Test] public void Color_hsl() { AssertPropertyParses("color", "hsl(245, 80%, 60%)"); }
        [Test] public void Color_hsla() { AssertPropertyParses("color", "hsla(245, 80%, 60%, 0.5)"); }
        [Test] public void Color_named() { AssertPropertyParses("color", "dodgerblue"); }
        [Test] public void Color_transparent() { AssertPropertyParses("background-color", "transparent"); }

        // --- At-rules --------------------------------------------------------

        [Test]
        public void AtRule_media_query_parses() {
            var sheet = CssParser.Parse("@media (min-width: 600px) { p { color: red; } }",
                new ParseOptions { ThrowOnError = true });
            Assert.That(sheet.Rules, Has.Count.EqualTo(1));
        }

        [Test]
        public void AtRule_keyframes_parses() {
            var sheet = CssParser.Parse("@keyframes fade { from { opacity: 0; } to { opacity: 1; } }",
                new ParseOptions { ThrowOnError = true });
            Assert.That(sheet.Rules, Has.Count.EqualTo(1));
        }

        [Test]
        public void AtRule_import_parses() {
            var sheet = CssParser.Parse("@import \"theme.css\";",
                new ParseOptions { ThrowOnError = true });
            Assert.That(sheet.Rules, Has.Count.EqualTo(1));
        }

        [Test]
        public void AtRule_supports_parses() {
            var sheet = CssParser.Parse("@supports (display: grid) { p { color: red; } }",
                new ParseOptions { ThrowOnError = true });
            Assert.That(sheet.Rules, Has.Count.EqualTo(1));
        }

        // --- HTML void elements ---------------------------------------------

        [Test] public void HtmlVoid_br() { Assert.That(HtmlElements.IsVoid("br"), Is.True); }
        [Test] public void HtmlVoid_hr() { Assert.That(HtmlElements.IsVoid("hr"), Is.True); }
        [Test] public void HtmlVoid_img() { Assert.That(HtmlElements.IsVoid("img"), Is.True); }
        [Test] public void HtmlVoid_input() { Assert.That(HtmlElements.IsVoid("input"), Is.True); }
        [Test] public void HtmlNonVoid_div() { Assert.That(HtmlElements.IsVoid("div"), Is.False); }
        [Test] public void HtmlNonVoid_p() { Assert.That(HtmlElements.IsVoid("p"), Is.False); }

        // --- Inheritance flags -----------------------------------------------

        [Test] public void Inherited_color() { Assert.That(CssProperties.IsInherited("color"), Is.True); }
        [Test] public void Inherited_font_family() { Assert.That(CssProperties.IsInherited("font-family"), Is.True); }
        [Test] public void NotInherited_width() { Assert.That(CssProperties.IsInherited("width"), Is.False); }
        [Test] public void NotInherited_background_color() { Assert.That(CssProperties.IsInherited("background-color"), Is.False); }

        // --- Named color count guard ----------------------------------------

        [Test]
        public void Named_colors_include_dodgerblue() {
            // Sanity check the named color table without leaking its internals.
            AssertPropertyParses("color", "dodgerblue");
            AssertPropertyParses("color", "rebeccapurple");
            AssertPropertyParses("color", "transparent");
        }
    }
}
