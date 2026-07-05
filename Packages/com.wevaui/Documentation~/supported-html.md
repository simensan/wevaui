# Supported HTML

[← Back to index](index.md)

Weva ships a **hand-rolled HTML parser** for an authored-input subset — no
HTML5 error-recovery surface. The parser produces a DOM of `Node`, `Element`,
`TextNode`, and `Document` types. Anything outside the subset below is either
ignored or fails loudly rather than silently miscomputing.

## Elements

| Category | Elements |
|---|---|
| Structural | `div`, `section`, `header`, `footer`, `nav`, `main`, `article`, `aside` |
| Text | `p`, `span`, `h1`–`h6`, `strong`, `em`, `b`, `i`, `u`, `code`, `small`, `br`, `hr` |
| Inline link | `a` |
| Lists | `ul`, `ol`, `li` |
| Form | `button`, `input`, `select`, `option`, `textarea`, `label`, `form` |
| Media | `img` |
| Tables | `table`, `thead`, `tbody`, `tfoot`, `tr`, `td`, `th`, `col`, `colgroup`, `caption` |
| Disclosure / dialog | `details`, `summary`, `dialog` |
| Composition | `template`, `slot`, `<template src="...">` imports |

`<head>`, `<title>`, `<meta>`, and `<link>` are recognized in the document
head; `<link rel="stylesheet">` pulls in a stylesheet by relative path.

### Form controls

`<input>` supports these `type=` values: `text`, `password`, `email`,
`number`, `search`, `tel`, `url`, `checkbox`, `radio`, `range`, `hidden`. The
`<form>` element groups controls and fires `on-submit` — there are no real
HTTP submit semantics. See [`AuthoringGuide.md`](AuthoringGuide.md) §4 for the
per-control behavior table.

### Tables, details/summary, dialog

These are implemented with the limitations noted in [Supported CSS](supported-css.md).
Runtime tables exist (including `border-collapse: collapse` winner resolution),
but advanced fragmentation and some collapsed-border painting are scoped out of
v1. `<details>`/`<summary>` get the UA-stylesheet `[open]` toggle visuals;
`<dialog>` supports modal/non-modal via `DialogElement.ShowModal()`.

### Deliberately omitted

`iframe`, `script`, top-level `<style>` blocks (inline `<style>` inside a
`<template>` is parsed but not yet wired into the cascade), `canvas`, `svg`,
`audio`, `video`. There is no JavaScript engine; interactivity comes from C#
controller binding, not DOM script.

## Attributes

- **Universal:** `id`, `class`, `style`, `hidden`, `title`, `tabindex`,
  `data-*`, `aria-*` (parsed and stored; `aria-*` is not yet consumed).

> **Accessibility status (v1).** `aria-*` attributes parse and are queryable
> from C#, but the engine does **not** drive a screen reader / OS accessibility
> tree — there is no assistive-tech surface in v1. What *does* work today:
> keyboard `Tab`/`Shift+Tab` focus, gamepad/`DirectionalNavigation`, and
> `:focus`/`:focus-visible` styling (see [AuthoringGuide §18](AuthoringGuide.md)).
> Build keyboard- and controller-navigable UI; don't rely on screen-reader
> semantics yet.

- **Form:** `name`, `value`, `placeholder`, `disabled`, `checked`, `min`,
  `max`, `step`, `required`, `readonly`.
- **Link:** `href` — fires a C# event; no navigation.
- **Image:** `src`, `alt`, `width`, `height`. `src` and CSS `url(...)` resolve
  through an `IImageRegistry` you own (see [`AuthoringGuide.md`](AuthoringGuide.md) §13),
  not a file path.
- **Event hooks:** `on-click`, `on-change`, `on-input`, `on-submit`,
  `on-focus`, `on-blur`, plus the pointer/keyboard/scroll kinds — bind to C#
  methods on the controller.
- **Binding/template attributes:** `data-each`, `data-key`,
  `data-class-<name>`, and `{{ }}` interpolation in any attribute value.

## Document structure rules

- A full `<!DOCTYPE html><html><head>…</head><body>…</body></html>` document
  parses, but a bare fragment (the `menu.html` example with just a `<link>` and
  a `<main>`) also works — the parser wraps loose content.
- **Self-closing is accepted on any element** (`<div/>` is legal — JSX-friendly).
  This is a deliberate divergence from HTML5, where only void elements
  self-close.
- **Unknown named entities pass through literally** rather than erroring.
- The tokenizer reports line/column diagnostics and resolves standard named
  and numeric character entities.

A built-in **user-agent stylesheet** supplies default displays (block vs.
inline), heading sizes, link color, list markers, and the `hidden` attribute,
matching Chrome's UA defaults where they apply.

---

Next: [Supported CSS](supported-css.md)
