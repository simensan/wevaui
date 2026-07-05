# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-06-10

Preview release for external evaluation. (Version held at 0.1.0 across iterations
so test projects pick up updates without manifest edits.)

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
