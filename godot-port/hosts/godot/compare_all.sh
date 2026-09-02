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
set -u
CORPUS="${1:?usage: compare_all.sh <corpus-dir> [size]}"
SIZE="${2:-1280x720}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RENDER="${WEVA_RENDER:-$HOME/weva/build-gcc/tools/weva_render/weva_render}"
GODOT="${GODOT_BIN:-$HOME/godot/godot}"

for html in "$CORPUS"/*.html; do
    base="$(basename "$html" .html)"
    css="${html%.html}.css"
    [ -f "$css" ] || css="-"
    out=$(GODOT_SILENCE_ROOT_WARNING=1 timeout 300 python3 "$ROOT/hosts/godot/compare_render.py" \
            "$html" "$css" --size "$SIZE" \
            --weva-render "$RENDER" --godot "$GODOT" \
            --project "$ROOT/hosts/godot/project" 2>&1)
    ink=$(printf '%s\n' "$out" | sed -n 's/.*ink disagrees *\([0-9]*\) px (\([0-9.]*\)%).*/\2/p')
    cov=$(printf '%s\n' "$out" | sed -n 's/.*ink coverage *software \([0-9]*\) px, godot \([0-9]*\) px.*/\1 \2/p')
    [ -n "$ink" ] || ink="ERR"
    printf '%-24s ink-disagree %6s%%  coverage %s\n' "$base" "$ink" "${cov:-?}"
done
