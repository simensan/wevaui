# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-09-13

One engine. The C# HTML/CSS engine that 0.1.x shipped is deleted; the
package is the Unity host for the Weva core (`libweva`, C++, behind a C ABI,
checked against Chrome), which the Godot addon also runs on.

### Fixed
- Identical byte-backed fonts share a private buffer across document lifetimes.
  Twelve fresh Inter loads previously retained about 718 MB in FontEngine;
  the warmed regression now stays flat. Caller mutations cannot alter the
  shared font data. Other native font caches can still be large.
- Linked and imported stylesheets retain their own image/font paths. URLs
  introduced by CSS variables resolve next to the stylesheet that uses them;
  baked linked sheets preserve the same origins.
- Separate linked and inspector stylesheets retain their own `@import` and
  parser boundaries. Appending CSS preserves markup stylesheet precedence
  before and after resizing. Asset readers receive core-resolved paths;
  relative file bases are applied once and can be cleared on reload.
- Unity input preserves emoji and other characters outside the BMP as whole
  Unicode characters. Focus and gamepad text-entry callbacks can disable or
  reload the document safely; remaining input stops after the handoff, with
  pending key releases delivered so auto-repeat cannot remain stuck.
- Linked stylesheets use the shared HTML parser for live documents and baked
  assets. Comments, raw text and template contents no longer produce spurious
  links; character references and quoted attributes resolve consistently.
  The core preserves raw-text/RCDATA contents and the first duplicate attribute.
- Multi-column layout now fragments direct text and inline elements, and
  honors column settings on inline-block containers in the shared core.
- Installed fonts retain their file identity in FontEngine's cache across
  document lifetimes. Twelve warmed backend recreations previously retained
  about 211 MB with Segoe UI; the regression test now shows no growth.
- Event callbacks can reload or disable a document safely: dispatch stops for
  the old tree, and recursive `PumpEvents()` calls defer to the next pump.
- Two-way controller bindings write through nested lists and dictionaries.
  `Bind(model)` persists when called while disabled and across disable/enable.
- Documented that assigning `WevaElement.Value` is programmatic and raises no
  input/change events; bound controls should be changed through their model.

### Migrating from 0.1.x
- `WevaDocument` keeps its name, its script GUID and its serialized fields
  (`documentAsset`, `stylesheetAssets`, `sortingOrder`,
  `prefersDarkColorScheme`): a scene binds to the new component unchanged.
  Remove the `IMGUIDocumentRenderer`, `UnityInputController` and
  `DevToolsOverlay` components (now missing scripts) from its GameObject.
- `[UIBind]`, `{{ path }}`, `data-each` / `data-key` / `$index`,
  `data-class-*`, `data-model`, `on-<event>="Name"`, `SetController` /
  `GetController<T>()` and `IBindingVersion` carry over. A handler that took
  an event argument takes the element's id: `void OnStart(string id)` (or
  nothing). A `bool` interpolates as `true`/`false`, not `True`/`False`.
- The C# DOM is gone: `Weva.Dom`, `Weva.Events`, `Weva.Forms`,
  `GetElementById`, `Rebuild()`, `[UIElement]`, manipulators, `ContextMenu`,
  `TooltipManager`, `WevaFonts`, `RendererBackend`, the IMGUI fallback and
  the F12 overlay. `doc.Query(selector)` returns a `WevaElement` (value,
  attributes, classes, focus, bounds, scroll, rows, dialogs, parent and
  children; stale after a reload, never throws); `Reload()` replaces
  `Rebuild()`; fonts are the component's `Font`/`Bold`/`Italic`/`Fallbacks`,
  `@font-face` and `RegisterFontFamily(name, font)`; DevTools is Window →
  Weva → Elements. `Weva.Native` is internal.
- `UIBatchedRendererFeature` keeps its name; `UIRendererFeature` is gone.
  Always Included Shaders needs `Hidden/Weva/NativeMesh` (the setup adds it).
- Package dependencies: URP and the Input System only (uGUI and Burst
  dropped).

### Added
- A shaper. The package shapes each run over the font's own OpenType
  tables (its GSUB read from the font bytes, GPOS anchors through
  FontEngine), asking the core for direction, mirroring, joining types and
  scripts (ABI minor 40): a right-to-left run comes back in visual order
  with mirrored brackets, Arabic letters take their `isol`/`init`/`medi`/
  `fina` forms and ligatures (`ccmp`, `rlig`, `calt`, `liga`; single,
  multiple, ligature, contextual and chaining contextual lookups in every
  format), a cursive script joins at its `curs` anchors, combining marks
  sit on their base and stack (anchors read from the font's GPOS), and the
  nine main Indic scripts shape by syllable — base consonant, half and
  below-base forms, the reph, a left matra before its consonant, a
  two-part vowel sign as its parts (ABI minor 42: Indic categories and
  canonical decomposition from the core). Before, every run was one glyph
  per code point in logical order. Not done: Sinhala, Khmer, Myanmar,
  Tibetan; a face given as a `Font` asset gets no substitutions (no bytes
  to read).
- Multi-column layout, in the core for both hosts: `column-count`,
  `column-width`, `column-gap`, `column-rule` (drawn in the gaps),
  `column-span: all`, `break-inside: avoid`, `break-before: column`, and
  balanced fragmentation between lines and whole blocks the way Blink
  balances — the height starts at the flow over the count, never below a
  paragraph's first two lines, and grows by the smallest shortage while
  the columns run out; a paragraph split across columns is where its lines
  are, as `getBoundingClientRect` reports it. Measured against Chrome on
  the multicol sample and nine probes (`Tools/oracle/corpus/hand/61-69`).
- Vertical writing modes, in the core for both hosts: `writing-mode:
  vertical-rl` and `vertical-lr`. A vertical box in a horizontal page is
  an orthogonal flow: its subtree is laid out on its side (block, inline,
  flex, grid and table alike), its inline size fitted against the block
  size available to it, and turned back into place; each text run paints a
  quarter turn clockwise with the line's over side on the right, as
  `text-orientation: mixed` does for Latin. Physical padding, borders and
  insets stay where the author put them; the user-agent sheet's paragraph
  and heading margins are flow-relative now (`margin-block`), as Chrome's
  are, so they sit left and right of a vertical paragraph. Measured against
  Chrome on the logical-properties sample. Not done: `sideways-*`, upright
  CJK, a horizontal box nested inside a vertical one, decorations,
  selection and the caret on a vertical run.
- A family the page names resolves to the installed font of that name:
  `font-family: "Segoe UI"` draws with Segoe UI where the machine has it,
  with its bold and italic files, as in a browser (the core lists the
  named families, ABI minor 41; the host registers the installed ones,
  never over a `@font-face` or a game-registered family). Under
  `SystemFontFallback`.
- `SystemFontFallback` (on by default): after the `Fallbacks`, the
  platform's UI, Indic and symbol fonts (Segoe UI, Nirmala UI and Segoe UI
  Symbol; Arial, Kohinoor Devanagari and Apple Symbols; DejaVu Sans)
  answer for a script or glyph none of the faces
  carry, as a browser reaches a system font. Hebrew and Arabic drew as
  nothing before, and so did the HUD sample's ⚔ and ☥.
- Input through the Input System: mouse, keyboard with held-key repeat,
  touch, gamepad navigation, IME composition; Chrome's 100 px wheel notch.
  Overlapping documents arbitrate the pointer by `SortingOrder`: the one on
  top takes it where it accepts it (everywhere, as in a browser); a HUD
  passes clicks through with `html, body { pointer-events: none }` and
  `pointer-events: auto` on its controls.
- `backdrop-filter` renders: the target is copied before each filtered
  element and its shape drawn through the blur and the colour matrix the
  core composed (`Hidden/Weva/NativeBackdrop`, also always-included).
- `<link rel="stylesheet">` resolved next to the document asset and baked
  for player builds; `BasePath` defaults to the asset's folder.
- `WevaElement` and `WevaEvent`; `PrefersDarkColorScheme`,
  `FollowScreenSafeArea`, `SetSafeAreaInsets`, `Cursor`, `AssetReader`,
  `InputConsumed`, `AcceptsKeyboard`, `WrapTab` / `TabbedOut`,
  `GamepadTextEntry` / `TextEntryRequested`.

### Fixed
- A `<template>`'s content is inert as in a browser: `Query`, an element's
  text and its children no longer see a `data-each` template's body before
  its rows are cloned.
- The inspector's "renderer feature missing" warning no longer fires in a
  fresh play session when the feature is present.
- `body { padding }` shrinks the content as in a browser. The user-agent
  sheet gave `html` and `body` `width: 100%`, so padding on either
  overflowed the viewport by the padding and every right-to-left line ran
  off the right edge; width is `auto` now (a block fills its container
  anyway), `height: 100%; margin: 0; overflow: hidden` stay.
- `backdrop-filter` in a player: the pass drew after URP's final blit, on
  the back buffer, which cannot be sampled for the copy. A frame with a
  backdrop draw now runs the pass after post-processing with an
  intermediate texture requested, so the copy has a texture to read; a
  frame without one still draws straight to the target.
- Small text no longer has glyphs sitting a pixel above or below their
  neighbours: the font backend reports a rasterised glyph's bearings as the
  bitmap's edge (the outline's fractional extents rounded the other way for
  about half the glyphs at 11–13 px).

### Platforms
- Native plugin for Windows x64. Other platforms follow as their CI jobs go
  green.

## [0.1.1] - 2026-07-05

Packaging only: no Runtime code changed between the two tags.

### Changed
- Minimum Unity lowered from 6000.4 to 6000.3, and the `unityRelease` pin
  dropped, so the package installs on any 6000.3 patch.

## [0.1.0] - 2026-06-10

Preview release for external evaluation. (The version was held at 0.1.0 across
many iterations so test projects picked up updates without manifest edits; the
0.1.1 tag above is the first bump since.)

### Added
- HTML/CSS-compatible UI layer for Unity: standard HTML elements, real CSS
  cascade, and a layout engine covering block, inline, flexbox and grid.
- URP rendering path with batched surface compositing (gamma-space sRGB).
- Incremental layout: subtree-scoped relayout under animation, including
  height-delta propagation through vertical stacking chains.
- **Bundled color emoji**: Noto Color Emoji (SIL OFL 1.1) is the default emoji
  font and renders out of the box — no local Segoe UI Emoji and no bake step.
  Override per-project by dropping a TTF at `Assets/UI/Fonts/NotoColorEmoji.ttf`.
- **Bundled monochrome symbols**: Noto Sans Symbols 2 (SIL OFL 1.1) covers the
  Geometric Shapes / Dingbats / Misc Symbols blocks (★ ◆ ▲ ● ♠ ✓ …) as the
  colorable mono fallback, replacing the proprietary Segoe UI Symbol path so
  those glyphs render in editor and builds without a Windows install.
- **`Weva.WevaFonts` API**: `Register(family, path)` for custom families and
  `SetDefault(path, …)` to replace the bundled Inter default. (Custom fonts also
  work via `Resources/Fonts/`, `@font-face`, and OS-installed font names.)
- Bundled fonts now live under `Runtime/Resources/Fonts/` and load via
  `Resources.Load`, so emoji and the default font resolve in player builds, not
  just the editor.
- `ScrollEventHandler.EnableViewportDragScroll` (default **off**): opt-in flag
  for pointer-drag panning of the whole viewport (off avoids HUDs sliding when
  dragged; element-level scroll containers and wheel/scrollbar are unaffected).
- `<input type="range">` now renders an accent-colored fill + a round draggable
  thumb (previously only the bare track painted).
- Sample: **Phase One Demo** (`Samples~/PhaseOneDemo`) — exercises the full
  parse → cascade → layout → paint → controller-binding → hot-reload pipeline.

### Changed
- DevTools overlay and FPS chip default **off** (F12 still toggles).
- `<button>` UA default is now Chrome's `display: inline-block` + `text-align:
  center` (was `inline-flex`). Labels center at any button width — including
  flex-grown segmented controls — without an author-bleeding `justify-content`;
  vertical centering inside an explicit `height` is handled by a layout pass.
- Layout: closed a sub-pixel height-jitter threshold gap so animated content
  stays on the incremental relayout path instead of falling back to full layout.
- Declared `com.unity.ugui` + `com.unity.burst` as dependencies (the
  TextCore/TMP font-sourcing path requires them; previously undeclared).

### Fixed
- Pixel-valued gradient color stops now resolve to the correct line-length
  fractions (fixes thin/1px gradient grid lines rendering wrong).
- AuditFonts window no longer logs a hard error when no pre-baked emoji atlas
  exists — emoji render via the bundled fallback regardless.

### Requirements
- Unity 6000.4
- Universal RP 17.0.0
- Input System 1.7.0

### Notes
- Preview release. APIs may change before 1.0.0.
