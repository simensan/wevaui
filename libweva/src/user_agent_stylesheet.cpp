#include "weva/user_agent_stylesheet.h"

namespace weva {

std::string_view user_agent_stylesheet_source() {
    static constexpr std::string_view kSource = R"CSS(
/* Game-UI default differs from browser default: the runtime always paints
   into a fixed viewport, so the natural shape for the root box is "fill the
   viewport, no margin". Authors writing `.hud { width: 100%; height: 100% }`
   expect 100% to mean "the full viewport" — but the browser default
   (`body { margin: 8px }`, body auto-sizing to content height) makes the
   height collapse to 0. We set height and margin on both html and body so
   100% bottoms out at the viewport without authors having to remember the
   html/body reset boilerplate. Width is left `auto`, as in a browser: a
   block fills its container anyway, and `auto` is what lets
   `body { padding: 32px }` shrink the content the way it does in Chrome
   (`width: 100%` plus padding overflowed the viewport by the padding, and
   every right-to-left line ran off the right edge). `overflow: hidden`
   matches the runtime's fixed viewport: anything outside it is invisible
   in a game camera anyway, and explicitly setting it here prevents
   accidental scroll-affordance creation on the root. */
html, body { height: 100%; margin: 0; overflow: hidden; }
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
[dir="rtl"] { direction: rtl; }
[dir="ltr"] { direction: ltr; }
/* HEAD and its descendants must not contribute to layout — title/meta/link
   would otherwise paint as inline text inside the body's flow. */
head, head *, title, meta, link, style, script, noscript, base { display: none; }

div, section, article, header, footer, nav, main, aside,
form, fieldset, ul, ol, hr, blockquote { display: block; }
li { display: list-item; }

/* Block margins are flow-relative, as in Chrome's own sheet: in a vertical
   writing mode a paragraph's margins sit on its left and right. */
p { display: block; margin-block-start: 1em; margin-block-end: 1em; }

h1 { display: block; font-size: 2em;    font-weight: bold; margin-block-start: 0.67em; margin-block-end: 0.67em; }
h2 { display: block; font-size: 1.5em;  font-weight: bold; margin-block-start: 0.83em; margin-block-end: 0.83em; }
h3 { display: block; font-size: 1.17em; font-weight: bold; margin-block-start: 1em;    margin-block-end: 1em; }
h4 { display: block;                    font-weight: bold; margin-block-start: 1.33em; margin-block-end: 1.33em; }
h5 { display: block; font-size: 0.83em; font-weight: bold; margin-block-start: 1.67em; margin-block-end: 1.67em; }
h6 { display: block; font-size: 0.67em; font-weight: bold; margin-block-start: 2.33em; margin-block-end: 2.33em; }

a, span, strong, em, b, i, u, code, small, br, label { display: inline; }

b, strong { font-weight: bold; }
i, em     { font-style: italic; }
u         { text-decoration: underline; }
code, kbd, samp { font-family: monospace; }
small     { font-size: 0.83em; }
pre       { display: block; font-family: monospace; white-space: pre; margin-block-start: 1em; margin-block-end: 1em; }

a { color: #0066cc; text-decoration: underline; }

ul, ol { padding-inline-start: 40px; margin-block-start: 1em; margin-block-end: 1em; }
ul { list-style-type: disc; }
ol { list-style-type: decimal; }

button, input, select, textarea, img { display: inline-block; }
/* Form controls reset inherited text effects in browser UA styles. Authors
   can opt back in with the normal cascade (including explicit inherit). */
button, input, select, textarea { letter-spacing: normal; word-spacing: normal;
    text-transform: none; text-indent: 0; text-shadow: none; }
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
/* Desktop browser small-control font (10pt at 96 CSS px/in). An authored
   font or font:inherit overrides this UA rule through the ordinary cascade. */
button { font: 13.333333333333333px sans-serif; box-sizing: border-box; padding: 1px 6px; border: 2px outset ButtonBorder;
         background-color: ButtonFace; color: ButtonText; display: inline-block; text-align: center; }
button:active { border-style: inset; }
button:active:disabled { border-style: outset; }
input, textarea, select { padding: 1px 2px; border: 1px solid #767676; }
/* Chrome UA: textarea content preserves newlines/spaces and soft-wraps
   (white-space: pre-wrap). Also the contract the multiline caret map
   relies on: with preserved whitespace, every character the line breaker
   DROPS from the painted runs is whitespace (hung trailing spaces,
   consumed newlines), so painted runs align back to model text indices
   deterministically (Forms.TextAreaCaretMap). */
textarea { white-space: pre-wrap; overflow-wrap: break-word; }
textarea[wrap="off" i] { white-space: pre; overflow-wrap: normal; }
/* Chrome UA: a textarea SCROLLS its content. Beyond the visual effect this
   settles its baseline — CSS 2.1 10.8.1 puts an inline-block's baseline at the
   bottom MARGIN edge once overflow is not `visible`, instead of at its last
   line box. Without it the baseline moved with the wrapped content, so a
   textarea holding text that wrapped past its rows dragged the inputs beside
   it off by a line, and the two engines disagreed about which line. */
textarea { overflow: auto; }

table { display: table; box-sizing: border-box; border-collapse: separate; border-spacing: 2px; }
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
   the language-appropriate pair (English: " and ''). Authors can override
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

/* ---- Runtime/Forms/FormControlStylesheet.cs, appended at the same UA origin
   after the base sheet, as UIDocumentBuilder does. Later declarations win
   among UA rules by document order, so these override the generic
   `input, textarea, select` padding/border above. */
input { display: inline-block; box-sizing: border-box; width: 218px; height: 34px; padding: 4px 8px; border: 1px solid #ccc; border-radius: 4px; font: inherit; }
input[type="checkbox"], input[type="radio"] { width: 16px; height: 16px; padding: 0; margin: 3px 3px 3px 4px; }
input[type="hidden"] { display: none; }
input[type="radio"] { border-radius: 8px; margin: 3px 3px 0 5px; }
textarea { display: inline-block; box-sizing: border-box; width: 218px; height: 90px; padding: 4px 8px; border: 1px solid #ccc; border-radius: 4px; font: inherit; }
/* Keep the themed default size overridable through width alone, like input
   and textarea. A UA min-width would also defeat an author's max-width. */
select { display: inline-block; box-sizing: border-box; width: 218px; height: 34px; padding: 4px 8px; border: 1px solid #ccc; border-radius: 4px; }
/* A closed <select> renders only the selected option's text via its own
   paint path; its <option> children are not laid out in flow. */
option { display: block; padding: 2px 4px; font-weight: normal; white-space: nowrap; }
optgroup { display: block; font-weight: bold; white-space: nowrap; }
optgroup option { padding-left: 20px; }
/* More rows than fit is the normal case for a keybind or server list, so it
   scrolls rather than hiding the rest behind an edge -- the wheel, the bar and
   the keyboard all follow from this one declaration. */
select[size], select[multiple] { overflow-y: auto; }
/* A list box shows its selection in the row itself -- there is no closed
   control to display it in, so without this a chosen row looks like every
   other one. Chrome uses the system highlight; this is a blue an author can
   override with `option:checked`, since the engine has no system colours. */
select[size] option:checked, select[multiple] option:checked {
  background: #3390ff; color: #ffffff;
}
select[size] optgroup, select[multiple] optgroup { display: block; }
dialog { display: none; position: fixed; padding: 16px; border: 1px solid #ccc; border-radius: 8px; background: white; }
dialog[open] { display: block; }
[popover] { display: none; position: fixed; inset: 0; margin: auto; width: fit-content; height: fit-content; padding: 8px 16px; border: 1px solid #ccc; border-radius: 4px; background: white; }
[popover]:popover-open { display: block; }
::backdrop { position: fixed; top: 0; right: 0; bottom: 0; left: 0; display: block; box-sizing: border-box; background: rgba(0, 0, 0, 0.5); pointer-events: none; }
:focus-visible { outline: 2px solid #2563eb; outline-offset: 2px; }
:disabled { opacity: 0.5; cursor: not-allowed; }
/* The element is the slider's interaction area. Its native rail and thumb are
   painted inside it; a UA background must not become a second, full-height rail.
   Author backgrounds and borders still use the ordinary box paint path. */
input[type="range"] { margin: 2px; width: 200px; height: 18px; padding: 0; border: 0; border-radius: 0; background: transparent; cursor: pointer; }
.ui-tooltip { padding: 4px 8px; border-radius: 4px; background: rgba(15, 23, 42, 0.95); color: #f8fafc; font-size: 12px; line-height: 1.3; max-width: 240px; pointer-events: none; }
.ui-menu { display: flex; flex-direction: column; min-width: 160px; padding: 4px 0; border: 1px solid #d1d5db; border-radius: 6px; background: white; box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15); font-size: 13px; }
.ui-menu-item { display: flex; align-items: center; padding: 6px 12px; cursor: pointer; gap: 8px; }
.ui-menu-item:hover { background: #f3f4f6; }
.ui-menu-item.is-focused { background: #e0e7ff; }
.ui-menu-item.is-disabled { opacity: 0.5; cursor: not-allowed; }
.ui-menu-item .ui-menu-label { flex: 1; }
.ui-menu-item .ui-menu-shortcut { color: #6b7280; font-size: 12px; }
.ui-menu-separator { height: 1px; background: #e5e7eb; margin: 4px 0; }
)CSS";
    return kSource;
}

} // namespace weva
