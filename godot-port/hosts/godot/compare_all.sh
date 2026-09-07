#!/usr/bin/env bash
# Runs compare_render.py over a corpus and prints one line per sample, worst
# first, so the render work can be ordered.
#
# This is the render-side counterpart to the layout oracle, and it is a BETTER
# check than comparing against Chrome screenshots: both sides consume the
# identical draw list from the same libweva build, so a difference is a
# difference between the BACKENDS with cascade, layout and tessellation held
# fixed. Chrome cannot play that role — under the synthetic metrics font its
# glyphs are solid boxes, and without it its text metrics differ from ours, so
# either way the comparison is dominated by text rather than by rendering.
#
# Both sides must use the SAME face: do NOT pass --engine-font, or Godot draws
# the engine's real font against the core's stub and every glyph disagrees.
#
# Usage: compare_all.sh <corpus-dir> [size]
set -uo pipefail
failures=0
CORPUS="${1:?usage: compare_all.sh <corpus-dir> [size]}"
SIZE="${2:-1280x720}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RENDER="${WEVA_RENDER:-$HOME/weva/build-gcc/tools/weva_render/weva_render}"
GODOT="${GODOT_BIN:-$HOME/godot/godot}"
shopt -s nullglob
pages=("$CORPUS"/*.html)
[ "${#pages[@]}" -gt 0 ] || { echo 'ERR no corpus pages' >&2; exit 1; }

for html in "${pages[@]}"; do
    base="$(basename "$html" .html)"
    css="${html%.html}.css"
    [ -f "$css" ] || css="-"
    if ! out=$(GODOT_SILENCE_ROOT_WARNING=1 timeout 300 python3 "$ROOT/hosts/godot/compare_render.py" \
            "$html" "$css" --size "$SIZE" \
            --weva-render "$RENDER" --godot "$GODOT" \
            --project "$ROOT/hosts/godot/project" 2>&1); then
        failures=$((failures + 1))
        printf 'ERR %s comparison failed\n%s\n' "$base" "$out" >&2
    fi
    ink=$(printf '%s\n' "$out" | sed -n 's/.*structural *\([0-9]*\) px (\([0-9.]*\)%).*/\2/p')
    over=$(printf '%s\n' "$out" | sed -n 's/.*over tolerance *\([0-9]*\) px (\([0-9.]*\)%).*/\2/p')
    [ -n "$ink" ] || { ink="ERR"; failures=$((failures + 1)); }
    [ -n "$over" ] || { over="ERR"; failures=$((failures + 1)); }
    # Structural is the gate — a shape drawn wrongly or not at all.
    # Over-tolerance is reported beside it because edge antialiasing lands
    # there and nowhere else, so a sample high in one and low in the other is
    # two rasterisers disagreeing at edges rather than a bug worth chasing.
    printf '%-24s struct %6s%%  over-tol %6s%%\n' "$base" "$ink" "$over"
done
[ "$failures" -eq 0 ]
