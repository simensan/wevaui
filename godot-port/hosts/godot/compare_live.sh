#!/usr/bin/env bash
# The backend comparison for the states that only exist while a user is doing
# something -- a cursor, a selection, a scrolled list, an open dropdown.
#
# compare_all.sh covers the corpus, but a corpus of static pages never reaches
# those paths: the dropdown in particular is painted AFTER the tree and with no
# scissor, which is unlike anything else the renderer does. Without this it was
# verified on the software side only, and the Godot host could have drawn it
# anywhere at all.
#
# Usage: compare_live.sh   (needs the same GODOT_BIN / WEVA_RENDER as
# compare_all.sh)
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
S=$ROOT/tools/oracle/corpus/samples
R=${WEVA_RENDER:-$HOME/weva/build-gcc/tools/weva_render/weva_render}
G=${GODOT_BIN:-$HOME/godot/godot}
run() {
    local label="$1"; shift
    local out
    out=$(GODOT_SILENCE_ROOT_WARNING=1 timeout 300 python3 "$ROOT/hosts/godot/compare_render.py" \
            "$S/forms-live.html" "$S/forms-live.css" --size 440x460 \
            --weva-render "$R" --godot "$G" --project "$ROOT/hosts/godot/project" \
            "$@" 2>&1)
    local ink over
    ink=$(printf '%s\n' "$out" | sed -n 's/.*structural *\([0-9]*\) px (\([0-9.]*\)%).*/\2/p')
    over=$(printf '%s\n' "$out" | sed -n 's/.*over tolerance *\([0-9]*\) px (\([0-9.]*\)%).*/\2/p')
    [ -n "$ink" ] || { ink="ERR"; printf '%s\n' "$out" | tail -5; }
    [ -n "$over" ] || over="ERR"
    printf '%-22s struct %6s%%  over-tol %6s%%\n' "$label" "$ink" "$over"
}
run idle
run focused-field   --focus='#name' --selection=11,17
run focused-area    --focus='#notes' --selection=2,14
run open-select     --open='#q'
run scrolled-list   --focus='#log' --scroll=40
