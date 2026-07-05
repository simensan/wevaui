using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Contract tests for the production `UserAgentStylesheet.Source`.
    //
    // Background: a stale UA rule for `<button>` (display: inline-flex +
    // justify-content: center + align-items: center) cascaded its centre
    // properties into author-overridden `display: flex` buttons, pushing
    // the first flex item into the middle of the row instead of
    // flex-start. The bug shipped without test coverage because the
    // engine's test helper uses a STRIPPED UserAgent that omits the
    // button rule; the production cascade was only exercised by the
    // FormControlSizingTests sizing path, which didn't notice
    // justify-content leakage.
    //
    // These tests pin spec-required defaults of `UserAgentStylesheet`
    // directly against `ComputedStyle.Get` so the next behaviour change
    // is caught at parse time rather than weeks later in a visible
    // real-world regression.
    //
    // Source: each test cites the spec section it pins. Where the engine
    // intentionally diverges (e.g. <html>/<body> default = full viewport,
    // not auto), the divergence is documented in `UserAgentStylesheet`
    // and the test pins Weva's choice rather than Chrome's.
    public class UserAgentDefaultsContractTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        // Build cascade with ONLY the production UA stylesheet — no author
        // CSS, no FormControlStylesheet — so every observed value comes
        // straight from `UserAgentStylesheet.Source`.
        static ComputedStyle ComputeWithUA(string html, string id) {
            var doc = Html(html);
            var sheets = new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(),
            };
            var engine = new CascadeEngine(sheets);
            return engine.Compute(doc.GetElementById(id));
        }

        // ─── <button> defaults (HTML §4.10.6 + Chrome UA) ─────────────────

        [Test]
        public void Button_display_is_inline_block() {
            // UA: `button { display: inline-block }` — Chrome's default. With
            // `text-align: center` this centres the label horizontally at ANY
            // button width (auto, explicit, or flex-grown); vertical centring
            // inside an explicit height is handled by BlockLayout's button
            // content-centering pass. The old `inline-flex` default
            // left-aligned labels on any button wider than its text and made
            // every button a flex container (justify-content bleed risk).
            var cs = ComputeWithUA("<button id=\"x\">Hi</button>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("inline-block"));
        }

        [Test]
        public void Button_text_align_is_center() {
            // Chrome UA: button { text-align: center }. This is how single-
            // line text content centres horizontally inside a button.
            var cs = ComputeWithUA("<button id=\"x\">Hi</button>", "x");
            Assert.That(cs.Get("text-align"), Is.EqualTo("center"));
        }

        [Test]
        public void Button_box_sizing_is_border_box() {
            // Chrome UA: native form controls use border-box sizing so
            // author min-width/width includes padding and border.
            var cs = ComputeWithUA("<button id=\"x\">Hi</button>", "x");
            Assert.That(cs.Get("box-sizing"), Is.EqualTo("border-box"));
        }

        [Test]
        public void Button_padding_default_is_2px_6px() {
            // Engine UA: button { padding: 2px 6px }. Documented as a
            // compact game-UI default vs Chrome's `1px 6px` — pinned to
            // detect drift from the stylesheet value.
            var cs = ComputeWithUA("<button id=\"x\">Hi</button>", "x");
            Assert.That(cs.Get("padding-top"), Is.EqualTo("2px"));
            Assert.That(cs.Get("padding-bottom"), Is.EqualTo("2px"));
            Assert.That(cs.Get("padding-left"), Is.EqualTo("6px"));
            Assert.That(cs.Get("padding-right"), Is.EqualTo("6px"));
        }

        [Test]
        public void Button_does_not_set_justify_content() {
            // CRITICAL — root cause of the HEROPICK-1 / hero-picker
            // bleed: when UA set justify-content: center on button, the
            // value cascaded into the author's `display: flex` override.
            // The UA must NOT set justify-content for <button> so its
            // computed value remains the spec default (`normal` → resolves
            // to `flex-start` in flex containers).
            var cs = ComputeWithUA("<button id=\"x\">Hi</button>", "x");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("normal"),
                "UA `button` rule must NOT set justify-content. If it does, " +
                "author-overridden `display: flex` buttons inherit the centre " +
                "justification and flush their first flex item to the middle " +
                "of the row — regression: hero-picker icons floating.");
        }

        [Test]
        public void Button_does_not_force_align_items_center() {
            // The UA no longer makes <button> a flex container, so it no longer
            // sets `align-items: center`. Vertical centring of single-line
            // button text inside an explicit height is done by BlockLayout's
            // button content-centering pass instead — keeping the cross-axis
            // property at its spec default so it can't bleed into an author
            // `display: flex` button's children.
            var cs = ComputeWithUA("<button id=\"x\">Hi</button>", "x");
            Assert.That(cs.Get("align-items"), Is.Not.EqualTo("center"));
        }

        // ─── <input> / <select> / <textarea> defaults ─────────────────────

        [Test]
        public void Input_display_is_inline_block() {
            // HTML §4.10.5: form controls are replaced elements rendered
            // as inline-block by default.
            var cs = ComputeWithUA("<input id=\"x\" type=\"text\">", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("inline-block"));
        }

        [Test]
        public void Select_display_is_inline_block() {
            var cs = ComputeWithUA("<select id=\"x\"></select>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("inline-block"));
        }

        [Test]
        public void Textarea_display_is_inline_block() {
            var cs = ComputeWithUA("<textarea id=\"x\"></textarea>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("inline-block"));
        }

        [Test]
        public void Img_display_is_inline_block() {
            // CSS Images L3 — replaced elements default to inline (or
            // inline-block per UA). Chrome treats <img> as inline-block.
            var cs = ComputeWithUA("<img id=\"x\" src=\"none.png\">", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("inline-block"));
        }

        // ─── Block-level defaults (HTML rendering UA) ─────────────────────

        [Test]
        public void Div_display_is_block() {
            var cs = ComputeWithUA("<div id=\"x\"></div>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("block"));
        }

        [Test]
        public void Paragraph_display_is_block() {
            var cs = ComputeWithUA("<p id=\"x\"></p>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("block"));
        }

        [Test]
        public void Header_display_is_block() {
            var cs = ComputeWithUA("<header id=\"x\"></header>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("block"));
        }

        [Test]
        public void Section_display_is_block() {
            var cs = ComputeWithUA("<section id=\"x\"></section>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("block"));
        }

        [Test]
        public void Nav_display_is_block() {
            var cs = ComputeWithUA("<nav id=\"x\"></nav>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("block"));
        }

        // ─── Inline-level defaults ─────────────────────────────────────────

        [Test]
        public void Span_display_is_inline() {
            var cs = ComputeWithUA("<span id=\"x\"></span>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("inline"));
        }

        [Test]
        public void Strong_display_is_inline() {
            var cs = ComputeWithUA("<strong id=\"x\"></strong>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("inline"));
        }

        [Test]
        public void Anchor_display_is_inline() {
            var cs = ComputeWithUA("<a id=\"x\"></a>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("inline"));
        }

        [Test]
        public void Code_uses_monospace_font_family() {
            // CSS UA: code, pre, samp, kbd use monospace family.
            var cs = ComputeWithUA("<code id=\"x\"></code>", "x");
            Assert.That(cs.Get("font-family"), Does.Contain("monospace"));
        }

        [Test]
        public void Pre_is_block_monospace_with_preserved_whitespace() {
            // HTML rendering spec: `pre` is a block box, uses the monospace
            // font, and PRESERVES whitespace (white-space: pre) so tabs and
            // runs of spaces render verbatim instead of collapsing.
            var cs = ComputeWithUA("<pre id=\"x\"></pre>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("block"));
            Assert.That(cs.Get("white-space"), Is.EqualTo("pre"));
            Assert.That(cs.Get("font-family"), Does.Contain("monospace"));
        }

        // ─── List defaults (CSS 2.1 §17 / HTML rendering) ─────────────────

        [Test]
        public void Ul_display_is_block() {
            var cs = ComputeWithUA("<ul id=\"x\"></ul>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("block"));
        }

        [Test]
        public void Ul_default_list_style_type_is_disc() {
            // CSS 2.1 §17 — `ul` initial list-style-type is `disc`.
            var cs = ComputeWithUA("<ul id=\"x\"></ul>", "x");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("disc"));
        }

        [Test]
        public void Ol_default_list_style_type_is_decimal() {
            // CSS 2.1 §17 — `ol` initial list-style-type is `decimal`.
            var cs = ComputeWithUA("<ol id=\"x\"></ol>", "x");
            Assert.That(cs.Get("list-style-type"), Is.EqualTo("decimal"));
        }

        [Test]
        public void Li_display_is_list_item() {
            // CSS 2.1 §17 — `<li>` defaults to display: list-item.
            var cs = ComputeWithUA("<ul><li id=\"x\"></li></ul>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("list-item"));
        }

        [Test]
        public void Ul_default_padding_left_is_40px() {
            // CSS UA: lists carry a 40px left padding so markers paint
            // outside the content edge.
            var cs = ComputeWithUA("<ul id=\"x\"></ul>", "x");
            Assert.That(cs.Get("padding-left"), Is.EqualTo("40px"));
        }

        // ─── <head> elements hidden ────────────────────────────────────────

        [Test]
        public void Style_element_is_hidden() {
            // UA: `template, link, meta, head, title, script, style { display: none }`
            var cs = ComputeWithUA("<head><style id=\"x\"></style></head>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("none"));
        }

        [Test]
        public void Hidden_attribute_renders_display_none() {
            // HTML5 [hidden] attribute → display: none. Pinned via UA selector.
            var cs = ComputeWithUA("<div id=\"x\" hidden></div>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("none"));
        }

        // ─── Table defaults (CSS 2.1 §17) ─────────────────────────────────

        [Test]
        public void Table_display_is_table() {
            var cs = ComputeWithUA("<table id=\"x\"></table>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("table"));
        }

        [Test]
        public void Tr_display_is_table_row() {
            var cs = ComputeWithUA("<table><tr id=\"x\"></tr></table>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("table-row"));
        }

        [Test]
        public void Td_display_is_table_cell() {
            var cs = ComputeWithUA("<table><tr><td id=\"x\"></td></tr></table>", "x");
            Assert.That(cs.Get("display"), Is.EqualTo("table-cell"));
        }

        [Test]
        public void Th_default_font_weight_is_bold() {
            // CSS 2.1 §17.5: th defaults to bold + center.
            var cs = ComputeWithUA("<table><tr><th id=\"x\"></th></tr></table>", "x");
            Assert.That(cs.Get("font-weight"), Is.EqualTo("bold"));
        }

        [Test]
        public void Th_default_text_align_is_center() {
            var cs = ComputeWithUA("<table><tr><th id=\"x\"></th></tr></table>", "x");
            Assert.That(cs.Get("text-align"), Is.EqualTo("center"));
        }

        // ─── Anchor element default colouring ──────────────────────────────

        [Test]
        public void Anchor_default_color_is_link_blue() {
            // Chrome UA: a { color: #0066cc } (engine matches via UA).
            var cs = ComputeWithUA("<a id=\"x\"></a>", "x");
            Assert.That(cs.Get("color"), Is.EqualTo("#0066cc"));
        }

        [Test]
        public void Anchor_default_text_decoration_line_is_underline() {
            // UA: `a { text-decoration: underline }`. The cascade expands
            // the `text-decoration` shorthand to longhands; query the
            // resolved longhand `text-decoration-line` to see the value.
            var cs = ComputeWithUA("<a id=\"x\"></a>", "x");
            Assert.That(cs.Get("text-decoration-line"), Does.Contain("underline"));
        }

        // ─── <small> font-size default ─────────────────────────────────────

        [Test]
        public void Small_font_size_is_relative() {
            // CSS UA: small { font-size: 0.83em }.
            var cs = ComputeWithUA("<small id=\"x\"></small>", "x");
            Assert.That(cs.Get("font-size"), Is.EqualTo("0.83em"));
        }
    }
}
