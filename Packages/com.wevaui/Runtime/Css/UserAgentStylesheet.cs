using Weva.Css.Cascade;
using Weva.Parsing;

namespace Weva.Css {
    public static class UserAgentStylesheet {
        public const string Source = @"
/* Game-UI default differs from browser default: in Unity the runtime always
   paints into a fixed viewport, so the natural shape for the root box is
   ""fill the viewport, no margin"". Authors writing `.hud { width: 100%;
   height: 100% }` expect 100% to mean ""the full viewport"" — but the
   browser default (`body { margin: 8px }`, body auto-sizing to content
   height) makes that collapse to 0. We set width/height/margin on both
   html and body so 100% bottoms out at the viewport without authors having
   to remember the html/body reset boilerplate. `overflow: hidden` matches
   the runtime's fixed viewport: anything outside it is invisible in a
   Unity Camera anyway, and explicitly setting it here prevents accidental
   scroll-affordance creation on the root. */
html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; }
/* CSS Values L4 §6.2: the html UA line-height is `normal`, which resolves
   to the font's own metric line-height. Chrome and every other browser
   uses this — we match. The previous explicit `1.36` was inherited into
   every text run as a unitless number and produced line-boxes ~19% taller
   than the font's metric line-height (Chrome's `normal` is ≈ 1.143 for
   the default sans-serif stack). The extra half-leading above + below
   each glyph row compounded in stacked-text flex containers (e.g. the
   a play-btn with PLAY over BEGIN STAGE), shifting the visible
   centroid off-centre even though justify-content:center placed the
   line-boxes correctly. PAINT-1 root cause. */
html { display: block; font-family: sans-serif; font-size: 16px; line-height: normal; color: black; }
body { display: block; }
[dir=""rtl""] { direction: rtl; }
[dir=""ltr""] { direction: ltr; }
/* HEAD and its descendants must not contribute to layout — title/meta/link
   would otherwise paint as inline text inside the body's flow. */
head, head *, title, meta, link, style, script, noscript, base { display: none; }

div, section, article, header, footer, nav, main, aside,
form, ul, ol, hr, blockquote { display: block; }
li { display: list-item; }

p { display: block; margin-top: 1em; margin-bottom: 1em; }

h1 { display: block; font-size: 2em;    font-weight: bold; margin-top: 0.67em; margin-bottom: 0.67em; }
h2 { display: block; font-size: 1.5em;  font-weight: bold; margin-top: 0.83em; margin-bottom: 0.83em; }
h3 { display: block; font-size: 1.17em; font-weight: bold; margin-top: 1em;    margin-bottom: 1em; }
h4 { display: block;                    font-weight: bold; margin-top: 1.33em; margin-bottom: 1.33em; }
h5 { display: block; font-size: 0.83em; font-weight: bold; margin-top: 1.67em; margin-bottom: 1.67em; }
h6 { display: block; font-size: 0.67em; font-weight: bold; margin-top: 2.33em; margin-bottom: 2.33em; }

a, span, strong, em, b, i, u, code, small, br, label { display: inline; }

b, strong { font-weight: bold; }
i, em     { font-style: italic; }
u         { text-decoration: underline; }
code      { font-family: monospace; }
small     { font-size: 0.83em; }
pre       { display: block; font-family: monospace; white-space: pre; margin-top: 1em; margin-bottom: 1em; }

a { color: #0066cc; text-decoration: underline; }

ul, ol { padding-left: 40px; margin-top: 1em; margin-bottom: 1em; }
ul { list-style-type: disc; }
ol { list-style-type: decimal; }

button, input, select, textarea, img { display: inline-block; }
/* Native <button> rendering: Chrome lays the label out in an inline-block
   box with the contents CENTERED. We match that with `display: inline-block`
   + `text-align: center` — which centres the label HORIZONTALLY at ANY button
   width (auto, explicit, or flex-grown), unlike the old `inline-flex` default
   whose flex content sat at main-start and left-aligned any button wider than
   its text. text-align is also bleed-free: it never affects an author
   `display: flex` button's children (the earlier global `justify-content:
   center` did — a hero-picker bleed incident). VERTICAL centring of a
   single line inside an explicit `height` is handled by ButtonContentCentering
   in layout (Chrome's anonymous centered content box), scoped to default-
   display buttons so author `display` overrides are untouched. */
button { box-sizing: border-box; padding: 2px 6px; display: inline-block; text-align: center; }
input, textarea, select { padding: 1px 2px; border: 1px solid #767676; }
/* Chrome UA: textarea content preserves newlines/spaces and soft-wraps
   (white-space: pre-wrap). Also the contract the multiline caret map
   relies on: with preserved whitespace, every character the line breaker
   DROPS from the painted runs is whitespace (hung trailing spaces,
   consumed newlines), so painted runs align back to model text indices
   deterministically (Forms.TextAreaCaretMap). */
textarea { white-space: pre-wrap; }

table { display: table; border-collapse: separate; border-spacing: 2px; }
thead { display: table-header-group; }
tbody { display: table-row-group; }
tfoot { display: table-footer-group; }
colgroup { display: table-column-group; }
col { display: table-column; }
tr { display: table-row; }
td, th { display: table-cell; padding: 1px; }
th { font-weight: bold; text-align: center; }
caption { display: table-caption; text-align: center; }

details > * { display: none; }
details > summary { display: list-item; cursor: default; }
details[open] > * { display: block; }
details[open] > summary { display: list-item; }

/* CSS HTML5 dialog UA stylesheet — matches Chrome's behaviour:
   a non-modal `<dialog open>` is absolutely positioned inside the initial
   containing block with its edges pinned and `margin: auto`, so the
   PositioningPass abs-pos algorithm centres the box horizontally
   automatically (and vertically when `height` is non-auto). Author CSS that
   sets explicit `top`/`left` overrides the UA inset on those sides; the
   remaining UA-supplied edges still participate in the centering math (see
   the modal-dialog snippet: `position: fixed; top: 80px; left: 80px;
   width: 240px` produces a horizontally-centred box because `right` and
   `bottom` are still 0 from this UA stylesheet). */
dialog {
  display: block;
  position: absolute;
  top: 0; right: 0; bottom: 0; left: 0;
  margin-top: auto; margin-right: auto; margin-bottom: auto; margin-left: auto;
  width: fit-content;
  height: fit-content;
  padding: 1em;
  border: solid;
  background-color: white;
  color: black;
}
dialog:not([open]) { display: none; }

progress { display: inline-block; width: 160px; height: 8px; }
meter { display: inline-block; width: 80px; height: 16px; }

template { display: none; }
link, meta, head, title, script, style { display: none; }
[hidden] { display: none; }

/* CSS Generated Content L3 §3 — <q> element UA rules.
   `q` is an inline element that generates typographic quotation marks
   automatically via open-quote / close-quote. `quotes: auto` resolves to
   the language-appropriate pair (English: "" and ''). Authors can override
   both `quotes` on `q` and suppress the generated marks with
   `q { quotes: none }`. Chrome's exact UA rule is:
     q { display: inline }
     q::before { content: open-quote }
     q::after  { content: close-quote }
   The `quotes` property is inherited so nested `<q>` elements automatically
   use the next nesting level's pair when the author provides multi-level pairs.
*/
q { display: inline; }
q::before { content: open-quote; }
q::after  { content: close-quote; }
";

        public static OriginatedStylesheet Parse() {
            var sheet = CssParser.Parse(Source, new ParseOptions { ThrowOnError = true });
            return OriginatedStylesheet.UserAgent(sheet);
        }
    }
}
