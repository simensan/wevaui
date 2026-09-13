# Weva Documentation

Weva is an HTML/CSS UI layer for Unity 6 / URP. You author UI in standard
`.html` and `.css` files; the engine — a C++ core shared with the Godot host,
checked against Chrome — parses them, runs a real CSS cascade, lays the
document out (block / inline / flex / grid / positioned), paints it, and the
package renders its draw list through a URP render pass.

The design rule is loud and simple: **if a feature has a well-known web
behavior, Weva's behavior matches it — or Weva doesn't ship it.** A subtly
different behavior is worse than none, because an AI model (or a developer who
knows the web) produces code that *looks* right and fails in surprising ways.
Anything not in the supported subset fails loudly rather than silently
miscomputing.

The Figma import plugin (`com.wevaui.figma`) lives in a separate repository
and is documented there.

## Status

Pre-1.0. `0.1.1` was the last release of the C# engine, frozen 2026-09-13;
`main` is the Unity host for the C++ core, and 1.0 ships one engine (see
[api-stability](api-stability.md) for what carries over).

## Contents

1. [Getting Started](getting-started.md) — install the package, create a
   document, load an HTML/CSS pair, set up URP rendering, size the viewport.
2. [Supported HTML](supported-html.md) — elements, attributes, and document
   structure rules.
3. [Supported CSS](supported-css.md) — properties, values, selectors, units,
   cascade behavior, at-rules, and the known divergences from Chrome.
4. [Animations & Transitions](animations-transitions.md) — `@keyframes`,
   `transition` / `animation` shorthands, easings, and which property kinds
   interpolate.
5. [Text & Fonts](text-and-fonts.md) — using your own fonts (`@font-face`, OS
   fonts, the `Weva.WevaFonts` API), emoji, monochrome symbols, and the
   default-face policy.
6. [Samples](samples.md) — the importable Phase One Demo and the repo's sample
   pages, and what each exercises.
7. [Troubleshooting](troubleshooting.md) — blank screen, styles not applying,
   bindings/clicks not firing, and other first-run snags.

## Author-facing guide vs. this reference

[`AuthoringGuide.md`](AuthoringGuide.md) is the task-oriented manual for
*building* UI — controller binding, events, forms, input, programmatic
updates, DevTools. The pages above are the *reference* for what HTML and CSS the
engine supports. Start with Getting Started, then reach for the Authoring Guide
when you build something real.

## Related documents

- [`CSS_FEATURES.md`](../CSS_FEATURES.md) — the full supported / partial /
  parse-only / missing CSS matrix.
- [`api-stability.md`](api-stability.md) — which types are covered by semantic
  versioning and which are engine internals that happen to be `public`.
- `LICENSE.md` — MIT, and `Third Party Notices.md` for the components the
  native plugin statically links.
