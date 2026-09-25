# Unsupported stylesheet rules

`WevaDocument.get_css_diagnostics()` and the C API
`weva_document_css_diagnostics` require ABI minor 24. Builds using earlier ABI
versions do not include this API; consult the addon's `build.json`.

Replacing a document's CSS emits Godot warnings for unsupported at-rules such
as `@page`. Repeated rules with the same name produce one diagnostic. The
rules remain ignored. `@font-face` is not among them since ABI minor 25: the
host loads and registers its sources (see GODOT_TEXT_SHAPING.md), and a source
that cannot load is reported by its own warning.

```gdscript
ui.css = stylesheet_text
for diagnostic in ui.get_css_diagnostics():
    print(diagnostic)
```

The query reads the compiled stylesheet diagnostics without updating layout,
painting, dispatching events or emitting warnings again. Replacing CSS removes
obsolete diagnostics. Viewport changes refresh the query's results when media
conditions change; they do not emit another console warning automatically.
Unmatched media/supports branches are omitted. Container-query branches are
compiled regardless of individual element matches, so their diagnostics can
appear even when no element currently satisfies the condition.

This currently covers unsupported at-rules, not unsupported properties,
selectors, malformed values or every partial feature. Supported keyframes and
encoding declarations are excluded from this list.

For C callers, a null buffer queries the required UTF-8 byte length, excluding
the terminator. Allocate that length plus one and call again. A nonempty output
buffer is always NUL-terminated, including on a null document. Truncation returns
the full required length. Each diagnostic occupies a separate line.
