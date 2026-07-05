# Weva Conformance Reference

> Current status: this reference is being reconciled with the implementation.
> Use [`CSS_FEATURE_AUDIT.md`](CSS_FEATURE_AUDIT.md) as the authoritative
> support/partial/stub/missing matrix. The engine now uses the CSS initial
> value `box-sizing: content-box`; older notes in this file that say
> `border-box` are stale.

Weva is a green-field HTML/CSS UI layer for Unity. Authors write standard `.html` and `.css`; the runtime parses, cascades, lays out, and paints with browser-faithful semantics for the supported subset. The current engine follows the CSS initial value `box-sizing: content-box`. Unknown properties are dropped with a warning, visual stubs emit diagnostics when authored with non-default values, unknown selectors throw a parse error, and unknown at-rules are skipped intentionally.

---

## HTML elements

The parser accepts the elements listed below. Unlisted tags parse as generic `Element` instances but receive no UA styling, no semantic behavior, and no special form handling. Self-closing slash syntax (`<div/>`) is accepted on every element (JSX-friendly). Void elements per the HTML spec are listed in the "Void" column. The "Default display" column shows the value set by the user-agent stylesheet.

### Structural

| Tag       | Default display | Void | Notable attributes |
|-----------|-----------------|------|--------------------|
| `div`     | `block`         | no   | universal          |
| `section` | `block`         | no   | universal          |
| `header`  | `block`         | no   | universal          |
| `footer`  | `block`         | no   | universal          |
| `nav`     | `block`         | no   | universal          |
| `main`    | `block`         | no   | universal          |
| `article` | `block`         | no   | universal          |
| `aside`   | `block`         | no   | universal          |
| `html`    | `block`         | no   | universal (root)   |
| `body`    | `block`         | no   | universal          |

### Text

| Tag       | Default display | Void | Notes |
|-----------|-----------------|------|-------|
| `p`       | `block`         | no   | top/bottom margin `1em` |
| `span`    | `inline`        | no   | |
| `h1`      | `block`         | no   | `font-size: 2em`, bold |
| `h2`      | `block`         | no   | `font-size: 1.5em`, bold |
| `h3`      | `block`         | no   | `font-size: 1.17em`, bold |
| `h4`      | `block`         | no   | bold |
| `h5`      | `block`         | no   | `font-size: 0.83em`, bold |
| `h6`      | `block`         | no   | `font-size: 0.67em`, bold |
| `strong`  | `inline`        | no   | bold |
| `em`      | `inline`        | no   | italic |
| `b`       | `inline`        | no   | bold |
| `i`       | `inline`        | no   | italic |
| `u`       | `inline`        | no   | underline |
| `code`    | `inline`        | no   | monospace |
| `small`   | `inline`        | no   | `font-size: 0.83em` |
| `br`      | `inline`        | yes  | hard line break in IFC |
| `hr`      | `block`         | yes  | horizontal rule |
| `blockquote` | `block`      | no   | |

### Inline link

| Tag | Default display | Void | Notable attributes |
|-----|-----------------|------|--------------------|
| `a` | `inline`        | no   | `href` (fires C# event; no navigation) |

### Lists

| Tag  | Default display | Void | Notes |
|------|-----------------|------|-------|
| `ul` | `block`         | no   | `padding-left: 40px` |
| `ol` | `block`         | no   | `padding-left: 40px` |
| `li` | `block`         | no   | |

### Form

| Tag       | Default display | Void | Notable attributes |
|-----------|-----------------|------|--------------------|
| `form`    | `block`         | no   | grouping only — no submit semantics |
| `button`  | `inline-block`  | no   | `disabled`, `type` |
| `input`   | `inline-block`  | yes  | `type`, `name`, `value`, `placeholder`, `disabled`, `checked`, `readonly`, `required`, `min`, `max`, `step`, `maxlength` |
| `select`  | `inline-block`  | no   | `name`, `value`, `disabled` |
| `option`  | `block`         | no   | `value`, `selected`, `disabled` |
| `textarea`| `inline-block`  | no   | `name`, `placeholder`, `disabled`, `readonly`, `required` |
| `label`   | `inline`        | no   | |

#### Accepted `<input type="…">` values

`text`, `password`, `number`, `checkbox`, `radio`, `range`, `hidden`, `email`, `tel`, `url`, `search`. Unknown types fall back to `text` semantics.

### Media

| Tag   | Default display | Void | Notable attributes |
|-------|-----------------|------|--------------------|
| `img` | `inline-block`  | yes  | `src`, `alt`, `width`, `height` |

### Generic / component

| Tag        | Default display | Void | Notes |
|------------|-----------------|------|-------|
| `template` | `none`          | no   | declares a reusable component (see Component system) |
| `slot`     | `inline`        | no   | placeholder inside a `<template>` body; `name` selects projected content |
| `link`     | (parsed only)   | yes  | `<link rel="stylesheet" href="…">` is recognized by the document loader |

### Deliberately omitted from v1

`iframe`, `script`, `style` (inline `<style>` blocks land in v2), `canvas`, `svg`, `audio`, `video`, `details`, `summary`, `picture`, `source`, `track`, `embed`, `object`, `noscript`. Table elements have partial layout support, including row groups, captions, `col`/`colgroup` width hints, `colspan`, and `rowspan`. Use `<img>` for static images; vector and animated media are deferred.

---

## HTML attributes

### Universal attributes

| Attribute   | Semantics |
|-------------|-----------|
| `id`        | unique per document; targeted by `#id` selectors and `[UIElement("id")]` |
| `class`     | space-separated tokens; targeted by `.class` selectors |
| `style`     | inline declaration block; participates in cascade with highest non-`!important` specificity |
| `hidden`    | UA stylesheet sets `display: none` via `[hidden]` selector |
| `data-*`    | parsed and stored verbatim; available via `Element.GetAttribute` |
| `aria-*`    | parsed and stored verbatim; not yet acted upon (architected for v2) |

### Form attributes

| Attribute     | Supported on                                | Semantics |
|---------------|---------------------------------------------|-----------|
| `name`        | `input`, `select`, `textarea`, `form`       | form-control identifier |
| `value`       | `input`, `option`, `select`                 | current value (two-way bound when `[UIBind]`) |
| `placeholder` | `input`, `textarea`                         | shown when value is empty; targetable via `:placeholder-shown` |
| `disabled`    | `input`, `button`, `select`, `textarea`, `option` | targetable via `:disabled` |
| `checked`     | `input[type=checkbox]`, `input[type=radio]` | targetable via `:checked` |
| `readonly`    | `input`, `textarea`                         | suppresses user editing; targetable via `:read-only` / `:read-write` |
| `required`    | `input`, `textarea`, `select`               | targetable via `:required` / `:optional`; feeds basic `:valid` / `:invalid` |
| `min`         | `input[type=number]`, `input[type=range]`   | numeric clamp |
| `max`         | `input[type=number]`, `input[type=range]`   | numeric clamp |
| `step`        | `input[type=number]`, `input[type=range]`   | increment |
| `maxlength`   | `input` (textual), `textarea`               | character cap |
| `selected`    | `option`                                    | initial selection |
| `type`        | `input`, `button`                           | see `<input>` types above |

### Link / image attributes

| Attribute | Supported on  | Semantics |
|-----------|---------------|-----------|
| `href`    | `a`, `link`   | on `<a>` fires a C# event; no automatic navigation |
| `rel`     | `link`        | `stylesheet` registers a CSS document |
| `src`     | `img`         | resolves against the asset path |
| `alt`     | `img`         | accessibility text (stored, not yet announced) |
| `width`   | `img`         | intrinsic-width hint in CSS pixels |
| `height`  | `img`         | intrinsic-height hint in CSS pixels |

### Event-binding attributes

Each resolves to a method on the controller object. Method signatures: zero parameters, one `UIEvent`, or one parameter of the matching typed event subclass (`PointerEvent`, `KeyboardEvent`, `FocusEvent`).

| Attribute    | Fires on `EventKind` | Typed event |
|--------------|----------------------|-------------|
| `on-click`   | `Click`              | `PointerEvent` |
| `on-change`  | `Change`             | `UIEvent` |
| `on-input`   | `Input`              | `UIEvent` |
| `on-submit`  | `Submit`             | `UIEvent` |
| `on-focus`   | `Focus`              | `FocusEvent` |
| `on-blur`    | `Blur`               | `FocusEvent` |

### Bookkeeping attributes (component system)

Set automatically by the `ComponentExpander` — authors should not write these.

| Attribute             | Set on                  | Meaning |
|-----------------------|-------------------------|---------|
| `data-uui-scope`      | every cloned descendant of an expanded component template | scope identifier rewritten into selector matching |
| `data-uui-host`       | the component host element after expansion | scope identifier; targetable via `:host` |
| `data-uui-expanded`   | the component host element after expansion | guards against re-expansion |

---

## CSS at-rules

| At-rule       | Syntax                                            | Scope       | Notes |
|---------------|---------------------------------------------------|-------------|-------|
| `@media`      | `@media <query> { <rules> }`                      | block       | full media-query language; see `@media` features below |
| `@keyframes`  | `@keyframes <name> { <selector> { <decls> } … }`  | block       | percentage and `from`/`to` selectors |
| `@import`     | `@import "<path>";`                               | declaration | resolves relative to the importing stylesheet; participates in load order |
| `@supports`   | `@supports <condition> { <rules> }`               | block       | feature queries for properties and `selector(...)`; parse-only stubs report unsupported |
| `@layer`      | `@layer <name> { <rules> }` / `@layer a, b;`      | block/stmt  | cascade layer ordering; dotted sublayers are flattened |
| `@container`  | `@container [name] <condition> { <rules> }`       | block       | size queries; nested rules have v1 limitations |
| `@scope`      | `@scope (<start>) [to (<end>)] { <rules> }`       | block       | nested scopes keep the innermost scope |
| `@font-face`  | `@font-face { font-family: ...; src: ...; }`      | block       | minimal family/src bridge |

### Deliberately omitted

`@page`, `@property`, `@charset`, `@namespace`. Inline `<style>` blocks are parsed by HTML but not wired into the cascade in v1.

---

## CSS selectors

Specificity follows the CSS Selectors Level 4 spec exactly (id > class/attribute/pseudo-class > type/pseudo-element). `!important` declarations win over non-`!important` of the same origin and cascade layer. Per Cascade L5 §6.4.1 step 5, an inline `!important` declaration (unlayered) loses to any layered `!important` rule.

### Simple selectors

| Syntax        | Matches |
|---------------|---------|
| `*`           | any element |
| `tag`         | elements with the given tag name (case-insensitive) |
| `.class`      | elements whose `class` token list contains the value |
| `#id`         | the element with the given `id` |

### Attribute selectors

| Syntax            | Matches |
|-------------------|---------|
| `[name]`          | element has the attribute |
| `[name=val]`      | attribute value equals `val` |
| `[name~=val]`     | whitespace-separated token list contains `val` |
| `[name\|=val]`    | value equals `val` or starts with `val-` |
| `[name^=val]`     | value starts with `val` |
| `[name$=val]`     | value ends with `val` |
| `[name*=val]`     | value contains `val` |

### Combinators

| Syntax     | Matches |
|------------|---------|
| `A B`      | descendant: `B` is a descendant of `A` |
| `A > B`    | child: `B` is a direct child of `A` |
| `A + B`    | adjacent sibling: `B` is the immediately following sibling of `A` |
| `A ~ B`    | general sibling: `B` follows `A` among siblings |

### Structural pseudo-classes

| Syntax              | Matches |
|---------------------|---------|
| `:first-child`      | element is the first child of its parent |
| `:last-child`       | element is the last child of its parent |
| `:only-child`       | element is the sole child of its parent |
| `:first-of-type`    | first element of its tag among siblings |
| `:last-of-type`     | last element of its tag among siblings |
| `:only-of-type`     | sole element of its tag among siblings |
| `:nth-child(n)`     | matches by index; accepts `An+B`, `odd`, `even` |
| `:nth-last-child(n)`| like `:nth-child` but counted from the end |
| `:nth-of-type(n)`   | by index among siblings of the same type |
| `:nth-last-of-type(n)` | by index among siblings of the same type, counted from the end |
| `:empty`            | no child elements or text content |
| `:not(list)`        | does not match any selector in the comma-separated selector list |
| `:is(list)`         | matches any of the comma-separated selectors |
| `:where(list)`      | like `:is`, but contributes specificity 0 |
| `:has(list)`        | element has a descendant/relative match from the selector list |
| `:root`             | the document root element |

### Language and direction pseudo-classes

| Syntax        | Matches |
|---------------|---------|
| `:lang(en)`   | element language inherited from `lang` / `xml:lang`, including dash-separated subranges such as `en-US` |
| `:dir(ltr)`   | element direction inherited from `dir`; `dir=auto` uses a first-strong-character heuristic |

### State pseudo-classes

| Syntax                 | Matches |
|------------------------|---------|
| `:hover`               | pointer is currently over the element |
| `:focus`               | element has keyboard focus |
| `:focus-visible`       | focused via keyboard / heuristic |
| `:focus-within`        | element or descendant has focus |
| `:active`              | element is being activated (pointer down) |
| `:disabled`            | form element with `disabled` attribute |
| `:enabled`             | supported form control without disabled state |
| `:checked`             | checkbox/radio with `checked` attribute |
| `:default`             | checked checkbox/radio, selected option, or first submit button in its owning form |
| `:required`            | input/select/textarea participating in required validation |
| `:optional`            | input/select/textarea without `required` |
| `:valid`               | supported control whose current value passes basic constraints |
| `:invalid`             | supported control failing required/email/url/number/pattern constraints |
| `:user-valid`          | valid supported control after user interaction through the dispatcher-backed form controllers |
| `:user-invalid`        | invalid supported control after user interaction through the dispatcher-backed form controllers |
| `:in-range`            | number/range input whose numeric value is within `min` / `max` |
| `:out-of-range`        | number/range input whose numeric value is outside `min` / `max` |
| `:read-only`           | element is not currently editable |
| `:read-write`          | editable text input/textarea or contenteditable element |
| `:placeholder-shown`   | input/textarea with empty value and a placeholder |
| `:any-link`            | `a`, `area`, or `link` element with an `href` attribute |
| `:link`                | unvisited hyperlink; without a host history provider, authored links match this state |
| `:visited`             | visited hyperlink; no browser history store is available in the core engine, so this currently never matches |
| `:target`              | the element whose `id` matches the current in-document fragment target set by an anchor default action or `EventDispatcher.SetTargetFragment` |

### Pseudo-elements

| Syntax           | Matches |
|------------------|---------|
| `::before`, `:before` | generated content before the element's children |
| `::after`, `:after`   | generated content after the element's children |
| `::placeholder`  | placeholder text inside an `<input>` / `<textarea>` |
| `::selection`    | the active text selection range |
| `::backdrop`     | viewport-filling backdrop for modal dialog / popover hosts |
| `::marker`       | generated list-item marker box |

### Scoping pseudo-classes

| Syntax              | Matches |
|---------------------|---------|
| `:scope`            | the document root outside `@scope`; inside `@scope`, the active scope-start element |
| `:host`             | the component host element (only inside a scoped stylesheet) |
| `:host(<selector>)` | the host element when it also matches `<selector>` |

### Deliberately omitted

`:current`, `:past`, `:future`, `::first-letter`, `::first-line`, `::file-selector-button`, `::part()`, `::slotted()`.

---

## CSS properties

Every property listed is registered in `Runtime/Css/Cascade/CssProperties.cs`. Anything else is dropped with a warning during cascade. The "Inh." column marks properties that inherit by default.

### Layout

| Property                  | Accepted values                                                             | Inh. | Initial         | Notes |
|---------------------------|-----------------------------------------------------------------------------|------|-----------------|-------|
| `display`                 | `block`, `inline`, `inline-block`, `flex`, `inline-flex`, `grid`, `inline-grid`, `none`, `contents` | no   | `inline`        | `inline-block` shrinks-to-fit and participates in the IFC; baseline = bottom of last line |
| `position`                | `static`, `relative`, `absolute`, `fixed`, `sticky`                         | no   | `static`        | `sticky` is scroll-aware; single-axis pinning (top wins over bottom, left over right) when both edges set |
| `top`, `right`, `bottom`, `left` | `<length>`, `<percentage>`, `auto`                                  | no   | `auto`          | |
| `z-index`                 | `<integer>`, `auto`                                                         | no   | `auto`          | |
| `overflow`                | `visible`, `hidden`, `scroll`, `auto`, `clip`                               | no   | `visible`       | shorthand for `overflow-x`/`overflow-y` |
| `overflow-x`              | `visible`, `hidden`, `scroll`, `auto`, `clip`                               | no   | `visible`       | |
| `overflow-y`              | `visible`, `hidden`, `scroll`, `auto`, `clip`                               | no   | `visible`       | |

### Flexbox

| Property                  | Accepted values                                                             | Inh. | Initial         |
|---------------------------|-----------------------------------------------------------------------------|------|-----------------|
| `flex`                    | `<flex-grow> <flex-shrink>? <flex-basis>?` shorthand                         | no   | `0 1 auto`      |
| `flex-direction`          | `row`, `row-reverse`, `column`, `column-reverse`                            | no   | `row`           |
| `flex-wrap`               | `nowrap`, `wrap`, `wrap-reverse`                                            | no   | `nowrap`        |
| `flex-flow`               | `<flex-direction>` and/or `<flex-wrap>`                                     | no   | `row nowrap`    |
| `flex-basis`              | `<length>`, `<percentage>`, `auto`, `content`                               | no   | `auto`          |
| `flex-grow`               | `<number>`                                                                  | no   | `0`             |
| `flex-shrink`             | `<number>`                                                                  | no   | `1`             |
| `justify-content`         | `flex-start`, `flex-end`, `center`, `space-between`, `space-around`, `space-evenly`, `start`, `end` | no | `flex-start` |
| `align-items`             | `stretch`, `flex-start`, `flex-end`, `center`, `baseline`                   | no   | `stretch`       |
| `align-self`              | `auto`, `stretch`, `flex-start`, `flex-end`, `center`, `baseline`           | no   | `auto`          |
| `align-content`           | `stretch`, `flex-start`, `flex-end`, `center`, `space-between`, `space-around`, `space-evenly` | no | `stretch` |
| `gap`                     | `<length>` or `<length> <length>`                                           | no   | `normal`        |
| `row-gap`                 | `<length>`, `<percentage>`, `normal`                                        | no   | `normal`        |
| `column-gap`              | `<length>`, `<percentage>`, `normal`                                        | no   | `normal`        |
| `order`                   | `<integer>`                                                                 | no   | `0`             |

### Grid

| Property                  | Accepted values                                                             | Inh. | Initial         |
|---------------------------|-----------------------------------------------------------------------------|------|-----------------|
| `grid-template-columns`   | `<track-list>` (`<length>`, `<percentage>`, `<flex>`, `auto`, `min-content`, `max-content`, `minmax(...)`, `repeat(...)`, `fit-content(...)`, named lines) | no | `none` |
| `grid-template-rows`      | `<track-list>` (same as columns)                                            | no   | `none`          |
| `grid-template-areas`     | quoted strings naming areas, one per row                                    | no   | `none`          |
| `grid-template`           | shorthand for areas + rows + columns                                        | no   | `none`          |
| `grid-column`             | `<line> / <line>` shorthand                                                 | no   | `auto`          |
| `grid-row`                | `<line> / <line>` shorthand                                                 | no   | `auto`          |
| `grid-column-start`       | `<line>` (number, name, `span <n>`, `auto`)                                 | no   | `auto`          |
| `grid-column-end`         | `<line>`                                                                    | no   | `auto`          |
| `grid-row-start`          | `<line>`                                                                    | no   | `auto`          |
| `grid-row-end`            | `<line>`                                                                    | no   | `auto`          |
| `grid-area`               | `<row-start> / <col-start> / <row-end> / <col-end>` or named area           | no   | `auto`          |
| `grid-auto-flow`          | `row`, `column`, `dense`, `row dense`, `column dense`                       | no   | `row`           |
| `grid-auto-columns`       | `<track-size>`                                                              | no   | `auto`          |
| `grid-auto-rows`          | `<track-size>`                                                              | no   | `auto`          |
| `place-items`             | `<align-items> <justify-items>?`                                            | no   | `normal legacy` |
| `place-content`           | `<align-content> <justify-content>?`                                        | no   | `normal`        |
| `place-self`              | `<align-self> <justify-self>?`                                              | no   | `auto`          |
| `justify-items`           | `start`, `end`, `center`, `stretch`, `legacy`                               | no   | `legacy`        |
| `justify-self`            | `auto`, `start`, `end`, `center`, `stretch`                                 | no   | `auto`          |

`<track-list>` accepts `repeat(<count>, <track-list>)` with `<count>` ∈ `<integer>` ∪ `auto-fill` ∪ `auto-fit`, `minmax(<min>, <max>)`, the `fr` flex unit, and named lines `[name]`.

### Tables

| Property                  | Accepted values                                                             | Inh. | Initial         | Notes |
|---------------------------|-----------------------------------------------------------------------------|------|-----------------|-------|
| `border-collapse`         | `separate`, `collapse`                                                      | yes  | `separate`      | `collapse` suppresses border-spacing in layout; border conflict painting is partial |
| `border-spacing`          | one or two `<length>` values                                                | yes  | `0`             | honored for separate-border table layout |
| `caption-side`            | `top`, `bottom`                                                             | yes  | `top`           | current layout places captions on top |
| `empty-cells`             | `show`, `hide`                                                              | yes  | `show`          | registered; hide behavior is incomplete |
| `table-layout`            | `auto`, `fixed`                                                             | no   | `auto`          | fixed layout uses `col`/`colgroup` and first-row width hints |
| `vertical-align`          | `baseline`, `top`, `middle`, `bottom`                                       | no   | `baseline`      | table cells support top/middle/bottom; baseline maps to top |

Table layout honors `colspan` and `rowspan` for grid placement and spanned cell geometry. `col`/`colgroup` width hints seed track sizing, and fixed layout uses column hints plus first-row cell widths. Collapsed border conflict resolution and collapsed border painting remain incomplete.

### Box model

| Property                  | Accepted values                                                             | Inh. | Initial            |
|---------------------------|-----------------------------------------------------------------------------|------|--------------------|
| `width`                   | `<length>`, `<percentage>`, `auto`                                          | no   | `auto`             |
| `height`                  | `<length>`, `<percentage>`, `auto`                                          | no   | `auto`             |
| `min-width`               | `<length>`, `<percentage>`, `auto`                                          | no   | `auto`             |
| `min-height`              | `<length>`, `<percentage>`, `auto`                                          | no   | `auto`             |
| `max-width`               | `<length>`, `<percentage>`, `none`                                          | no   | `none`             |
| `max-height`              | `<length>`, `<percentage>`, `none`                                          | no   | `none`             |
| `inline-size`             | `<length>`, `<percentage>`, `auto`                                          | no   | `auto`             |
| `block-size`              | `<length>`, `<percentage>`, `auto`                                          | no   | `auto`             |
| `min-inline-size` / `min-block-size` | `<length>`, `<percentage>`, `auto`                                | no   | `auto`             |
| `max-inline-size` / `max-block-size` | `<length>`, `<percentage>`, `none`                                | no   | `none`             |
| `padding`                 | 1–4 `<length>`/`<percentage>` (TRBL shorthand)                              | no   | `0`                |
| `padding-top`/`-right`/`-bottom`/`-left` | `<length>`, `<percentage>`                                   | no   | `0`                |
| `padding-inline` / `padding-block` | one or two `<length>`/`<percentage>` values                          | no   | `0`                |
| `padding-inline-start`/`-end`, `padding-block-start`/`-end` | `<length>`, `<percentage>`                  | no   | `0`                |
| `margin`                  | 1–4 `<length>`/`<percentage>`/`auto` (TRBL shorthand)                       | no   | `0`                |
| `margin-top`/`-right`/`-bottom`/`-left`  | `<length>`, `<percentage>`, `auto`                           | no   | `0`                |
| `margin-inline` / `margin-block` | one or two `<length>`/`<percentage>`/`auto` values                      | no   | `0`                |
| `margin-inline-start`/`-end`, `margin-block-start`/`-end` | `<length>`, `<percentage>`, `auto`             | no   | `0`                |
| `border`                  | `<border-width> <border-style> <color>` shorthand                            | no   | `medium none currentColor` |
| `border-width`            | `<length>`, `thin`, `medium`, `thick`                                       | no   | `medium`           |
| `border-style`            | `none`, `solid`, `dashed`, `dotted` (others map to `solid` with a warning)   | no   | `none`             |
| `border-color`            | `<color>`                                                                   | no   | `currentColor`     |
| `border-top` / `-right` / `-bottom` / `-left` | shorthand                                                | no   | `medium none currentColor` |
| `border-{side}-width`     | `<length>`, `thin`, `medium`, `thick`                                       | no   | `medium`           |
| `border-{side}-style`     | `none`, `solid`, `dashed`, `dotted`                                          | no   | `none`             |
| `border-{side}-color`     | `<color>`                                                                   | no   | `currentColor`     |
| `border-inline` / `border-block` | shorthand for logical start/end sides                                  | no   | `medium none currentColor` |
| `border-inline-start`/`-end`, `border-block-start`/`-end` | shorthand for one logical side                    | no   | `medium none currentColor` |
| `border-{inline,block}-*-width/style/color` | logical border side longhands                                | no   | side initial       |
| `border-radius`           | 1–4 `<length>`/`<percentage>` (corner shorthand)                            | no   | `0`                |
| `border-{corner}-radius`  | `<length>`, `<percentage>`                                                  | no   | `0`                |
| `border-start-start-radius`, `border-start-end-radius`, `border-end-start-radius`, `border-end-end-radius` | `<length>`, `<percentage>` | no | `0` |
| `box-sizing`              | `content-box`, `border-box`                                                 | no   | `content-box` |

### Logical axes

Logical sizes, insets, margins, padding, borders, and corner radii are remapped to physical properties during cascade resolution. Horizontal `direction: rtl` maps inline-start to the physical right edge and inline-end to the physical left edge; horizontal LTR maps inline-start to left and inline-end to right. `writing-mode: vertical-rl` / `vertical-lr` / `sideways-rl` / `sideways-lr` remap logical edges and sizes, but text shaping remains horizontal.

| Property                  | Accepted values                                                             | Inh. | Initial          |
|---------------------------|-----------------------------------------------------------------------------|------|------------------|
| `direction`               | `ltr`, `rtl`                                                                | yes  | `ltr`            |
| `writing-mode`            | `horizontal-tb`, `vertical-rl`, `vertical-lr`, `sideways-rl`, `sideways-lr`  | yes  | `horizontal-tb`  |
| `unicode-bidi`            | `normal`, `embed`, `isolate`, `bidi-override`, `isolate-override`, `plaintext` | no | `normal`         |
| `inset-inline` / `inset-block` | one or two `<length>`/`<percentage>`/`auto` values                    | no   | `auto`           |
| `inset-inline-start`/`-end`, `inset-block-start`/`-end` | `<length>`, `<percentage>`, `auto`              | no   | `auto`           |

### Typography

| Property                  | Accepted values                                                             | Inh. | Initial               |
|---------------------------|-----------------------------------------------------------------------------|------|-----------------------|
| `color`                   | `<color>`                                                                   | yes  | `black`               |
| `font-family`             | comma-separated `<string>`/`<ident>` list                                   | yes  | `sans-serif`          |
| `font-size`               | `<length>`, `<percentage>`, `xx-small`…`xx-large`, `smaller`, `larger`     | yes  | `16px`                |
| `font-weight`             | `<integer 100–900>`, `normal`, `bold`, `lighter`, `bolder`                  | yes  | `normal`              |
| `font-style`              | `normal`, `italic`, `oblique`                                               | yes  | `normal`              |
| `font-variant`            | `normal`, `small-caps`                                                       | yes  | `normal`              |
| `font`                    | shorthand: `<style> <variant> <weight> <size>[/<line-height>] <family>`     | yes  | (composite)           |
| `line-height`             | `<number>`, `<length>`, `<percentage>`, `normal`                            | yes  | `normal`              |
| `letter-spacing`          | `<length>`, `normal`                                                        | yes  | `normal`              |
| `word-spacing`            | `<length>`, `normal`                                                        | yes  | `normal`              |
| `text-align`              | `left`, `right`, `center`, `justify`, `start`, `end`                         | yes  | `start`               |
| `text-align-last`         | `auto`, `left`, `right`, `center`, `justify`, `start`, `end`                 | yes  | `auto`                |
| `text-indent`             | `<length>`, `<percentage>`                                                   | yes  | `0`                   |
| `text-wrap`               | `wrap`, `nowrap`                                                             | yes  | `wrap`                |
| `text-transform`          | `none`, `uppercase`, `lowercase`, `capitalize`                              | yes  | `none`                |
| `text-decoration`         | `none`, `underline`, `line-through`, `overline`, plus optional style/color   | no   | `none`                |
| `text-decoration-line`    | `none`, `underline`, `line-through`, `overline`                              | no   | `none`                |
| `text-decoration-style`   | `solid`, `dashed`, `dotted`, `wavy`, `double`                                | no   | `solid`               |
| `text-decoration-color`   | `<color>`                                                                   | no   | `currentColor`        |
| `text-overflow`           | `clip`, `ellipsis`                                                          | no   | `clip`                |
| `white-space`             | `normal`, `nowrap`, `pre`, `pre-wrap`                                       | yes  | `normal`              |
| `tab-size`                | positive `<number>`                                                          | yes  | `8`                   |
| `hyphens`                 | `none`, `manual`, `auto`                                                     | yes  | `manual`              |

### Backgrounds

| Property                  | Accepted values                                                             | Inh. | Initial         |
|---------------------------|-----------------------------------------------------------------------------|------|-----------------|
| `background`              | shorthand: `<color>`, `<image>`, position/size, repeat, clip                | no   | `none`          |
| `background-color`        | `<color>`                                                                   | no   | `transparent`   |
| `background-image`        | `none`, `url(...)`, `linear-gradient(...)`, `radial-gradient(...)`, comma list | no | `none`          |
| `background-size`         | `<length>`, `<percentage>`, `auto`, `cover`, `contain`                      | no   | `auto`          |
| `background-position`     | `<length>`/`<percentage>` pair, or `top`/`right`/`bottom`/`left`/`center`    | no   | `0% 0%`         |
| `background-repeat`       | `repeat`, `no-repeat`, `repeat-x`, `repeat-y`, `space`, `round`              | no   | `repeat`        |
| `background-clip`         | `border-box`, `padding-box`, `content-box`                                  | no   | `border-box`    |
| `background-origin`       | `border-box`, `padding-box`, `content-box`                                  | no   | `padding-box`   |
| `background-attachment`   | `scroll`, `fixed`, `local`                                                  | no   | `scroll`        |

### Effects

| Property                  | Accepted values                                                             | Inh. | Initial         | Notes |
|---------------------------|-----------------------------------------------------------------------------|------|-----------------|-------|
| `opacity`                 | `<number 0–1>`                                                              | no   | `1`             | |
| `visibility`              | `visible`, `hidden`, `collapse`                                             | yes  | `visible`       | |
| `cursor`                  | `auto`, `default`, `pointer`, `text`, `move`, etc.                          | yes  | `auto`          | |
| `box-shadow`              | `none`, or comma list of `<offset-x> <offset-y> <blur>? <spread>? <color>? inset?` | no | `none`     | outset and inset shadows both rendered |
| `transform`               | `none`, or space list of transform functions (see CSS functions)            | no   | `none`          | |
| `transform-origin`        | `<length>`/`<percentage>` for X and Y, optional Z `<length>`                | no   | `50% 50% 0`     | |
| `filter`                  | `none`, or space list of filter functions (see CSS functions)               | no   | `none`          | |
| `backdrop-filter`         | `none`, or space list of filter functions (see CSS functions)               | no   | `none`          | URP path samples the current color target; edge expansion and `drop-shadow()` remain partial |
| `clip-path`               | `none`, `inset()`, `circle()`, `ellipse()`, `polygon()`                     | no   | `none`          | basic-shape paint clipping; no `path()`/SVG clip source |
| `mask` / `mask-*`         | `mask-image`, mode/repeat/position/size/origin/clip/composite longhands     | no   | per CSS masking | layered gradient masks render; URL masks use geometry without full texture sampling; URP uploads first four layers |

### Animation

| Property                    | Accepted values                                                           | Inh. | Initial                    |
|-----------------------------|---------------------------------------------------------------------------|------|----------------------------|
| `transition`                | shorthand of `<property> <duration> <timing-function> <delay>`, comma list| no   | `all 0s ease 0s` (literal canonical form emitted by the runtime; `CssAnimationRunner` string-matches this exact value as the no-transition fast-path early-out — any edit to this string must be mirrored in `CssAnimationRunner.ParseTransitionSpecsFor` to preserve the optimization) |
| `transition-property`       | `all`, `none`, comma list of property names                               | no   | `all`                      |
| `transition-duration`       | `<time>` (`s`, `ms`), comma list                                          | no   | `0s`                       |
| `transition-timing-function`| `linear`, `ease`, `ease-in`, `ease-out`, `ease-in-out`, `step-start`, `step-end`, `cubic-bezier(...)`, `steps(...)` | no | `ease` |
| `transition-delay`          | `<time>`, comma list                                                      | no   | `0s`                       |
| `animation`                 | shorthand of name + duration + timing + delay + count + direction + fill + state | no | `none 0s ease 0s 1 normal none running` (literal canonical form emitted by the runtime; `CssAnimationRunner` string-matches this exact value as the no-animation fast-path early-out — any edit to this string must be mirrored in `CssAnimationRunner.UpdateAnimationsFor` to preserve the optimization) |
| `animation-name`            | `none`, `<keyframes-name>`                                                | no   | `none`                     |
| `animation-duration`        | `<time>`                                                                  | no   | `0s`                       |
| `animation-timing-function` | (same as `transition-timing-function`)                                    | no   | `ease`                     |
| `animation-delay`           | `<time>`                                                                  | no   | `0s`                       |
| `animation-iteration-count` | `<number>`, `infinite`                                                    | no   | `1`                        |
| `animation-direction`       | `normal`, `reverse`, `alternate`, `alternate-reverse`                     | no   | `normal`                   |
| `animation-fill-mode`       | `none`, `forwards`, `backwards`, `both`                                   | no   | `none`                     |
| `animation-play-state`      | `running`, `paused`                                                       | no   | `running`                  |

### Custom properties

| Syntax                    | Accepted values                                                             | Inh. | Notes |
|---------------------------|-----------------------------------------------------------------------------|------|-------|
| `--<name>: <value>;`      | any token sequence; resolved by `var()`                                     | yes  | inherited like a normal CSS custom property |

### Wide keywords on every property

`inherit`, `initial`, `unset`, `revert`, and `revert-layer` are accepted on every property and resolved by the cascade; `revert`/`revert-layer` perform real origin/layer rollback (Cascade L5), falling back to `initial` when no rollback target exists.

### Deliberately omitted

`columns`, `column-count`, `column-width`, `column-gap` (the multi-column kind; flex/grid `column-gap` is supported), `column-rule`, vertical text shaping, full bidi reordering, dictionary hyphenation, `text-orientation`, `clip`, `mix-blend-mode`, `background-blend-mode`, `content-visibility`, `touch-action`, `resize`, `appearance`, `quotes`, counters, and full generated-content semantics. `direction` / logical properties exist for cascade and horizontal layout; `writing-mode` remaps logical axes but does not rotate or vertically shape text. `clip-path` is limited to basic shapes; `mask` URL source-pixel sampling remains partial.

---

## CSS data types

| Type            | Definition                                                                  | Example |
|-----------------|-----------------------------------------------------------------------------|---------|
| `<length>`      | numeric distance with a unit (see length units below) or `0`                | `16px`, `1.5em`, `0` |
| `<percentage>`  | number followed by `%`; resolves against the property's reference length    | `50%` |
| `<number>`      | dimensionless real number                                                   | `1.5` |
| `<integer>`     | dimensionless whole number                                                  | `42` |
| `<color>`       | `#hex`, `rgb()`, `rgba()`, `hsl()`, `hsla()`, named color, `currentColor`, `transparent` | `#4f46e5`, `rgb(79 70 229)`, `dodgerblue` |
| `<image>`       | `url(<string>)`, `linear-gradient(...)`, `radial-gradient(...)`             | `linear-gradient(90deg, red, blue)` |
| `<keyword>`     | predefined identifier the property accepts                                  | `auto`, `none` |
| `<ident>`       | author-defined identifier (`<custom-ident>`)                                | `my-animation` |
| `<string>`      | quoted string                                                               | `"Helvetica"`, `'foo'` |
| `<url>`         | `url(<string>)` reference                                                   | `url("logo.png")` |
| `<time>`        | `<number>s` or `<number>ms`                                                  | `200ms`, `0.5s` |
| `<angle>`       | `<number>deg`, `<number>rad`, `<number>turn`, `<number>grad`                | `45deg`, `0.25turn` |
| `<resolution>`  | `<number>dpi`, `<number>dppx`, `<number>dpcm` (media queries only)          | `2dppx` |
| `<calc()>`      | arithmetic over lengths/percentages/numbers                                 | `calc(100% - 32px)` |
| `<var()>`       | reference to a custom property, with optional fallback                      | `var(--accent, blue)` |

### Length units

`px`, `em`, `rem`, `%`, `vh`, `vw`, `vmin`, `vmax`, `pt`, `pc`, `in`, `cm`, `mm`, `ch`, `ex`. The `fr` unit is accepted only inside `<track-list>` for grid templates.

### Named colors

149 named colors per CSS Color 4 (`aliceblue`, `antiquewhite`, …, `yellowgreen`) plus `transparent`. Names are case-insensitive. Unknown names fail to parse.

---

## CSS functions

### Variable / arithmetic

| Function          | Signature                                                                | Example |
|-------------------|--------------------------------------------------------------------------|---------|
| `var()`           | `var(--name)` or `var(--name, <fallback>)`                              | `var(--accent, blue)` |
| `calc()`          | arbitrary `+ - * /` over lengths/percentages/numbers                    | `calc(100% - 2 * 16px)` |
| `min()`           | `min(<value>, <value>, …)`                                              | `min(100%, 600px)` |
| `max()`           | `max(<value>, <value>, …)`                                              | `max(50px, 5vw)` |
| `clamp()`         | `clamp(<min>, <preferred>, <max>)`                                      | `clamp(12px, 2vw, 24px)` |

### Colors

| Function          | Signature                                                                | Example |
|-------------------|--------------------------------------------------------------------------|---------|
| `rgb()`           | `rgb(<r>, <g>, <b>)` or `rgb(<r> <g> <b>)`                              | `rgb(79, 70, 229)` |
| `rgba()`          | `rgba(<r>, <g>, <b>, <a>)`                                              | `rgba(0, 0, 0, 0.5)` |
| `hsl()`           | `hsl(<h>, <s>%, <l>%)` or modern slash syntax                           | `hsl(245, 80%, 60%)` |
| `hsla()`          | `hsla(<h>, <s>%, <l>%, <a>)`                                            | `hsla(245, 80%, 60%, 0.5)` |

`#hex` accepts 3, 4, 6, and 8 hex digits.

### Images

| Function           | Signature                                                                | Example |
|--------------------|--------------------------------------------------------------------------|---------|
| `url()`            | `url("<path>")`                                                          | `url("logo.png")` |
| `linear-gradient()`| `linear-gradient([<angle> | to <side-or-corner>], <color-stop-list>)`    | `linear-gradient(90deg, red, blue)` |
| `radial-gradient()`| `radial-gradient([<shape>] [<size>] [at <position>], <color-stop-list>)` | `radial-gradient(circle at center, red, transparent)` |

### Easing

| Function          | Signature                                                                | Example |
|-------------------|--------------------------------------------------------------------------|---------|
| `cubic-bezier()`  | `cubic-bezier(<x1>, <y1>, <x2>, <y2>)`; `x1`/`x2` ∈ `[0, 1]`            | `cubic-bezier(0.25, 0.1, 0.25, 1)` |
| `steps()`         | `steps(<count>)` or `steps(<count>, <position>)` where position ∈ `start`/`end`/`jump-start`/`jump-end`/`jump-both`/`jump-none` | `steps(4, end)` |

Identifier easings: `linear`, `ease`, `ease-in`, `ease-out`, `ease-in-out`, `step-start`, `step-end`.

### Transforms

| Function         | Signature                                                  | Example |
|------------------|------------------------------------------------------------|---------|
| `translate()`    | `translate(<length>)` or `translate(<length>, <length>)`   | `translate(8px, -4px)` |
| `translateX()`   | `translateX(<length>)`                                     | `translateX(8px)` |
| `translateY()`   | `translateY(<length>)`                                     | `translateY(-4px)` |
| `scale()`        | `scale(<sx>)` or `scale(<sx>, <sy>)`                       | `scale(0.97)` |
| `scaleX()`       | `scaleX(<sx>)`                                             | `scaleX(2)` |
| `scaleY()`       | `scaleY(<sy>)`                                             | `scaleY(0.5)` |
| `rotate()`       | `rotate(<angle>)`                                          | `rotate(45deg)` |
| `skew()`         | `skew(<angle>)` or `skew(<ax>, <ay>)`                      | `skew(10deg, 5deg)` |
| `skewX()`        | `skewX(<angle>)`                                           | `skewX(10deg)` |
| `skewY()`        | `skewY(<angle>)`                                           | `skewY(5deg)` |
| `matrix()`       | `matrix(a, b, c, d, tx, ty)`                               | `matrix(1, 0, 0, 1, 8, 0)` |

3D transform functions (`translate3d`, `scale3d`, `rotate3d`, `matrix3d`, `perspective`) are not supported; use the 2D forms.

### Filters

| Function         | Signature                                                  | Example |
|------------------|------------------------------------------------------------|---------|
| `blur()`         | `blur(<length>)`                                           | `blur(4px)` |
| `brightness()`   | `brightness(<number>)` or `brightness(<percentage>)`       | `brightness(1.2)` |
| `contrast()`     | `contrast(<number>)` or `contrast(<percentage>)`           | `contrast(0.8)` |
| `grayscale()`    | `grayscale(<number>)` or `grayscale(<percentage>)`         | `grayscale(100%)` |
| `opacity()`      | `opacity(<number>)` or `opacity(<percentage>)`             | `opacity(0.5)` |
| `saturate()`     | `saturate(<number>)` or `saturate(<percentage>)`           | `saturate(2)` |
| `hue-rotate()`   | `hue-rotate(<angle>)`                                      | `hue-rotate(45deg)` |
| `invert()`       | `invert(<number>)` or `invert(<percentage>)`               | `invert(100%)` |
| `sepia()`        | `sepia(<number>)` or `sepia(<percentage>)`                 | `sepia(60%)` |
| `drop-shadow()`  | `drop-shadow(<offset-x> <offset-y> <blur>? <color>?)`      | `drop-shadow(0 2px 4px rgba(0,0,0,0.3))` |

`backdrop-filter` uses the same filter function grammar as `filter`. In URP it snapshots the current color target behind the element and composites the filtered result inside the element bounds and border radius.

### Grid track functions

`repeat(<count>, <track-list>)`, `minmax(<min>, <max>)`, `fit-content(<length-percentage>)`. `<count>` accepts integers, `auto-fill`, and `auto-fit`. The `fr` flex unit is accepted only inside grid track lists.

---

## `@media` features

The query is evaluated against the **containing UI surface** (the Weva rendering area), not the OS window. This keeps split-screen and embedded panels predictable. Each feature accepts a bare form (truthiness test) and `min-`/`max-` ranged forms where applicable.

| Feature                   | Accepted values                                       | Notes |
|---------------------------|-------------------------------------------------------|-------|
| `width`                   | `<length>`                                            | `min-width` and `max-width` ranged forms |
| `height`                  | `<length>`                                            | `min-height` / `max-height` |
| `aspect-ratio`            | `<integer>/<integer>` or single `<number>`            | `min-aspect-ratio` / `max-aspect-ratio` |
| `orientation`             | `portrait`, `landscape`                               | derived from surface width vs height |
| `resolution`              | `<resolution>` (`dpi`, `dppx`, `dpcm`)                | `min-resolution` / `max-resolution` |
| `prefers-color-scheme`    | `light`, `dark`                                       | sourced from `MediaContext.ColorScheme` |
| `prefers-reduced-motion`  | `no-preference`, `reduce`                             | sourced from `MediaContext.PrefersReducedMotion` |
| `hover`                   | `none`, `hover`                                       | also `any-hover` (alias) |
| `pointer`                 | `none`, `coarse`, `fine`                              | also `any-pointer` (alias) |

Logical combinators `and`, `or`, `not` and comma-separated query lists are supported. Unknown features evaluate to `false` rather than throwing.

---

## Component system

Weva components are pure HTML/CSS — `<template id="<tag>">…</template>` declares a custom element that can be instantiated by writing `<<tag>>…</<tag>>` anywhere in a document.

### Template declaration

```html
<template id="card">
  <article class="card">
    <header><slot name="title">Untitled</slot></header>
    <div class="body"><slot></slot></div>
  </article>
</template>
```

- `<template>` itself has `display: none` per the UA stylesheet.
- The `id` attribute names the component tag.
- A template body may contain any number of `<slot>` placeholders.
- `<slot name="...">` projects light-DOM children whose `slot="..."` attribute matches; the unnamed slot receives the rest.
- A slot's children are rendered as fallback content when no projection matches.

### Instantiation

```html
<card>
  <h2 slot="title">Inventory</h2>
  <p>You have 12 items.</p>
</card>
```

The `ComponentExpander` clones the template body, projects light-DOM nodes into slots, and replaces the host's children with the cloned tree. The host element keeps its tag, attributes, and event listeners.

### Scoping

When a stylesheet is registered as a component scope, `SelectorScoper` rewrites every selector so it only matches inside that component's expanded subtree:

- Bookkeeping attributes (set automatically): `data-uui-scope` is stamped on every cloned descendant; `data-uui-host` is stamped on the host element; `data-uui-expanded` guards against re-expansion.
- `:host` in a scoped stylesheet matches the host element of that component.
- `:host(<selector>)` matches the host when it also matches `<selector>`. Comma-separated selector lists inside `:host(...)` expand correctly.
- Slot-projected children retain their original (parent-scoped) attribute set, so the parent component's rules still match them and the inner component's rules do not.

### Safety

The expander enforces a max recursion depth (default 32) and detects template cycles. Self-referential templates (e.g. a `<template id="button">` containing `<button>`) are detected and short-circuited.

---

## Binding system

Bindings are scanned at mount time by `BindingScanner.Scan(document, controller)`. The controller is an arbitrary `object` (commonly an `IBindingController`-marker class or `MonoBehaviour`).

### Text and attribute substitution

`{{ Path.Sub.Property }}` inside text content or attribute values resolves against the controller. Path segments are dot-separated and walk public fields/properties (and dictionary keys). Bindings re-evaluate when `Bindings.Update()` is called (typically once per frame).

```html
<p>Coins: <span>{{ CoinCount }}</span></p>
<img src="{{ Player.AvatarUrl }}" alt="{{ Player.Name }}" />
```

### `[UIBind]` for two-way form binding

Marking a controller field or property with `[UIBind]` makes it eligible for two-way binding from form controls (`<input>`, `<textarea>`, `<select>`). The binding is matched by `name` attribute.

```csharp
public class SettingsController {
    [UIBind] public float Volume;
    [UIBind] public bool Subtitles;
}
```

```html
<input name="Volume" type="range" min="0" max="1" step="0.05" />
<input name="Subtitles" type="checkbox" />
```

### `[UIElement("id")]` for element references

Marking a field with `[UIElement("some-id")]` populates the field with a wrapper around the element matching `id="some-id"` after mount.

```csharp
[UIElement("start-button")] public ButtonElement StartButton;
```

### `on-*` event attributes

Each event attribute names a method on the controller. Resolution is reflective; any access modifier is allowed; static and instance methods both qualify. The handler may have:

- zero parameters,
- one `UIEvent` parameter (base class),
- one parameter of the matching typed event subclass: `PointerEvent` for `Click`, `KeyboardEvent` for keyboard events, `FocusEvent` for `Focus`/`Blur`.

A handler with two or more parameters is rejected at scan time. A method name that doesn't resolve throws `BindingException`.

```html
<button on-click="OnStart">Start</button>
<input on-input="OnVolumeChanged" />
```

```csharp
void OnStart() { /* zero-arg */ }
void OnVolumeChanged(UIEvent e) { /* base */ }
void OnPointerDown(PointerEvent e) { /* typed */ }
```

---

## Events

Events flow through three phases — capture, at-target, bubble — exactly as in the DOM. All of the events below bubble. Click synthesis uses "down-target equals up-target" (the spec uses deepest-common-ancestor; documented as a v1 simplification).

| `EventKind`     | Triggered when                                                    | Typed class      |
|-----------------|-------------------------------------------------------------------|------------------|
| `PointerDown`   | pointer button pressed over an element                            | `PointerEvent`   |
| `PointerUp`     | pointer button released                                           | `PointerEvent`   |
| `PointerMove`   | pointer moves                                                     | `PointerEvent`   |
| `PointerEnter`  | pointer enters an element's geometry                              | `PointerEvent`   |
| `PointerLeave`  | pointer leaves an element's geometry                              | `PointerEvent`   |
| `Click`         | matching `PointerDown`/`PointerUp` on the same target             | `PointerEvent`   |
| `KeyDown`       | key pressed while the element has focus                           | `KeyboardEvent`  |
| `KeyUp`         | key released                                                      | `KeyboardEvent`  |
| `KeyPress`      | (enum present, not dispatched in v1 — needs IME/text-input layer) | `KeyboardEvent`  |
| `Focus`         | element receives focus                                            | `FocusEvent`     |
| `Blur`          | element loses focus                                               | `FocusEvent`     |
| `Change`        | committed value change on form control                            | `UIEvent`        |
| `Input`         | per-character/incremental value change                            | `UIEvent`        |
| `Submit`        | `<form>` submit gesture                                           | `UIEvent`        |

`UIEvent` exposes `Target`, `CurrentTarget`, `Phase`, `TimestampSeconds`, `Bubbles`, `PreventDefault()`, `StopPropagation()`, `StopImmediatePropagation()`, `DefaultPrevented`, `PropagationStopped`, `ImmediatePropagationStopped`. `PointerEvent` adds position, button, modifier, and pointer-id fields. `KeyboardEvent` adds key, code, modifier, and repeat fields. `FocusEvent` adds the related target.

---

## Default styles (UA stylesheet)

The complete user-agent stylesheet shipped in `Runtime/Css/UserAgentStylesheet.cs`:

```css
html { display: block; font-family: sans-serif; font-size: 16px; line-height: 1.36; color: black; }
body { display: block; margin: 8px; }

div, section, article, header, footer, nav, main, aside,
form, ul, ol, li, hr, blockquote { display: block; }

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

a { color: #0066cc; text-decoration: underline; }

ul, ol { padding-left: 40px; margin-top: 1em; margin-bottom: 1em; }

button, input, select, textarea, img { display: inline-block; }
button { padding: 2px 6px; }
input, textarea, select { padding: 1px 2px; border: 1px solid #767676; }

template { display: none; }
[hidden] { display: none; }
```

Note: `box-sizing: content-box` is the CSS initial value. Authors can opt into the common reset with `* { box-sizing: border-box }`.

---

## Deliberately omitted from v1

Each line is one decision; if you reach for a feature here, expect "fail loudly."

- **Layout (block/inline):** No margin collapsing between block siblings. Long unbreakable words overflow rather than break mid-word. `inline-block` is treated as block at all levels (true inline-block sizing/baseline alignment is deferred). Block-level descendants inside an inline element are skipped (no inline-splitting). Anonymous block boxes carry no `Style`. `text-align: justify` distributes space evenly across spaces with no last-line exclusion. Nested em compounding walks one parent level only.
- **Layout (positioning):** `position: sticky` is scroll-aware via `StickyResolver`; v1 simplification is single-axis pinning only (when both top/bottom are set, top wins; same for left/right). Both-pinned absolute auto-sizing (`top: 0; bottom: 0`) doesn't iterate to reconcile with intrinsic sizes. Absolute boxes stretched by both pinned edges don't re-flow their interior. Absolute/fixed inside flex are not removed from flex flow. Positioned descendants with `z-index: auto` do *not* create their own stacking context (only `z-index ≠ auto` does, for relative/absolute; fixed/sticky always do).
- **Layout (flex):** `min-content`/`max-content` keywords treated as `auto`. `aspect-ratio` re-derives auto heights after stretch/grow. Row-flex `baseline` alignment uses real item baselines (column flexes synthesise per spec). Item min/max main-size constraints are single-pass (no clamp loop). When flex sets a child's main size different from `style.width`/`height`, the box is resized but its interior is not re-flowed. Longhand `flex-grow: 0`/`flex-shrink: 1`/`flex-basis: auto` at initial values don't override a user-set `flex` shorthand. Text directly inside a flex container falls into normal anonymous-block flow rather than becoming an anonymous flex item.
- **Box-sizing default:** `content-box`, matching the CSS initial value. Use `* { box-sizing: border-box }` when authoring with the common web reset.
- **Paint converter:** Reads longhand properties only — `border: 1px solid red` shorthand isn't expanded by the cascade yet, so authors must write `border-{side}-{prop}` explicitly.
- **Color:** Named colors win when an `<ident>` is encountered as a value (no CSS context where `red` should be a non-color keyword in v1).
- **HTML:** Self-closing accepted on every element (`<div/>` legal, JSX-friendly). Unknown named entities pass through literally.
- **Events:** Click synthesis = down-target equals up-target (browsers use deepest common ancestor). No explicit pointer-capture API yet. `KeyPress` enum present but not dispatched. Active flag set on the literal hit element rather than activation chain.
- **HTML elements not in v1:** `iframe`, `script`, inline `<style>`, `canvas`, `svg`, `audio`, `video`, `picture`. Table elements, `details`/`summary`, and `dialog` exist with the limitations documented above.
- **CSS at-rules not in v1:** `@page`, `@property`, `@charset`, `@namespace`. `@font-face`, `@supports`, `@layer`, `@container`, and `@scope` exist with the limitations documented in `CSS_FEATURE_AUDIT.md`.
- **CSS not in v1:** multi-column, vertical text shaping, full bidi reordering, dictionary hyphenation, `mix-blend-mode`, CSS Houdini typed properties, counters, and full generated-content semantics. Logical properties, `direction: rtl`, and `writing-mode` axis remapping exist as partial engine support; floats, tables, object-fit, aspect-ratio, outlines, text shadows, lists, basic `clip-path`, layered masks, `backdrop-filter`, and container queries exist as supported or partial engine features; see `CSS_FEATURE_AUDIT.md`.
- **Selectors not in v1:** `:current`, `:past`, `:future`, `::first-letter`, `::first-line`, `::file-selector-button`, `::part()`, `::slotted()`.

---

## Performance posture

Target: **1000 styled elements at 144 Hz** on Apple M1 / Ryzen 5 5600 / RTX 3050-tier hardware, with `:hover` rules, gradients, shadows, and one transition active. The reactivity foundation is in place across the cascade, layout, and paint stages — every dirty element is recomputed; clean elements are reused from version-keyed caches. Layer caching and a GPU compositor model are deferred to Phase 7 (perf rewrite). Steady-state allocation is the explicit goal: layout/paint share pooled scratch buffers and from-scratch correctness oracles are preserved alongside the incremental paths. See PLAN.md §11 for current progress and the v1 simplification list reflected in this document.

---

## Looking up a feature

If you're checking whether `X` is supported, look in the table above. If it isn't there, assume it isn't supported. If you think it should be, open an issue with the use case — it is much easier to add a feature than to undo a half-supported one. The reference is intentionally short: anything outside it will fail loudly so failures surface during authoring rather than at runtime.
