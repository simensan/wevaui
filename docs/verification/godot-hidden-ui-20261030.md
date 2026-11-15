# Godot automatic hidden work — October 30, 2026

Godot's automatic `_process` updated layout/paint/textures even when its
WevaDocument, or an ancestor Control, was hidden. It now defers those automatic
updates. Pending mutations remain in the document and apply when shown.
The monotonic input timestamp is still advanced while hidden, so the hidden
interval is not fed back as a held-input tick when showing the document.
CSS animation time pauses during automatic hidden processing.

Explicit `update_document(dt)` and geometry queries remain available while
hidden. A loading phase can call `update_document(0)` to prepare layout, glyphs
and textures, then show the retained document. This changes host scheduling,
not the shared core's CSS behavior or ABI.

## Evidence

The expanded timing scene first failed on the unchanged DLL: 39 checks,
3 failures, including both direct hiding and a hidden ancestor. That evidence
is retained. After rebuilding the Godot extension:

- Timing: 39 checks, zero failures. Ten frames of pending style writes while
  hidden produce zero automatic core updates; explicit geometry flushes once,
  explicit paint preparation works, and showing applies the latest width.
  Five frames under a hidden ancestor likewise produce zero automatic updates.
- Bindings: 186 checks, zero failures.
- Transition reversal/cancellation: 32 / 18 checks, zero failures.
- Typeahead/text autoscroll/select autoscroll: 48 / 81 / 33 checks, zero failures.
- Retained-canvas scene completes on Vulkan and its hide/show capture restores
  the same RGB pixels as the preceding clipped state. This is a capture check,
  not a new assertion count for all scene phases.
- A separate real Vulkan hide–explicit-prewarm–show scenario checks pending
  bindings and saves an inspected image showing the updated text and 420px bar.

The first retained-canvas invocation incorrectly used `--headless`, which
cannot service its awaited post-draw event. Its 90-second timeout is preserved;
the corrected Vulkan run succeeds. This was runner misuse, not an engine test
failure. Earlier passing headless timing/binding checks were not repeated.

Windows Godot 4.7 official `5b4e0cb0f`, RTX 5080, Vulkan Forward Mobile. This is
ASCII scene/input/transition coverage, not the full qualified Unicode/export
gate. Build log contains no compiler warnings. Core/Unity sources are unchanged,
so their prior gates were not rerun. No frame-time percentage is claimed:
the counter regression proves the hidden automatic updates are eliminated.

## Unity and host-engine scope

Host-engine already defers hidden updates in both direct and threaded paths after
the [previous fix](host-engine-hidden-ui-20261030.md). Godot now avoids equivalent
automatic work while preserving its explicit prewarming API.

Unity's `OnDisable` unregisters rendering and releases its native document;
disabled components receive no automatic `Update`. Its existing disabled-
binding/re-enable regression covers restoration. That lifecycle therefore does
not have this hidden automatic-update defect, but it does not retain resources
like Godot hide/show. No Unity API or lifecycle change was made. CSS-hidden
content is a separate core behavior and was not modified.

Usage is documented in `hosts/godot/ADDON_README.md`; lifecycle boundaries are
recorded in `docs/INPUT_PARITY.md`. The [JSON receipt](godot-hidden-ui-20261030.json)
records log hashes, DLL identity and capture paths. Raw evidence is under
`.utmp/render-audit-20261030/godot-hidden-*` with the two retained runners and
the explicit visual scenario. All owned test processes finished or were
terminated by their subprocess timeout.

Cold glyph preparation, a convenient host-engine hidden-prewarm workflow, matched
Chrome rendering and long-duration lifecycle coverage remain open.
