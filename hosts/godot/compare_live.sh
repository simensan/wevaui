#!/usr/bin/env bash
# The backend comparison for the states that only exist while a user is doing
# something -- a cursor, a selection, a scrolled list, an open dropdown, a
# hovered row, a pressed button, a modal dialog, a popover, a tooltip.
#
# compare_all.sh covers the corpus, but a corpus of static pages never reaches
# those paths: the dropdown in particular is painted AFTER the tree and with no
# scissor, which is unlike anything else the renderer does. Without this it was
# verified on the software side only, and the Godot host could have drawn it
# anywhere at all.
#
# Usage: compare_live.sh   (needs the same GODOT_BIN / WEVA_RENDER as
# compare_all.sh)
set -uo pipefail
failures=0
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
S=$ROOT/Tools/oracle/corpus/samples
R=${WEVA_RENDER:-${WEVA_BUILD_GCC:-$HOME/weva/build-gcc}/Tools/weva_render/weva_render}
G=${GODOT_BIN:-$HOME/godot/godot}
run() {
    local label="$1"; shift
    local out
    if ! out=$(GODOT_SILENCE_ROOT_WARNING=1 timeout 300 python3 "$ROOT/hosts/godot/compare_render.py" \
            "$S/forms-live.html" "$S/forms-live.css" --size 440x460 \
            --weva-render "$R" --godot "$G" --project "$ROOT/hosts/godot/project" \
            "$@" 2>&1); then
        failures=$((failures + 1))
        printf 'ERR %s comparison failed\n%s\n' "$label" "$out" >&2
    fi
    local ink over
    ink=$(printf '%s\n' "$out" | sed -n 's/.*structural *\([0-9]*\) px (\([0-9.]*\)%).*/\2/p')
    over=$(printf '%s\n' "$out" | sed -n 's/.*over tolerance *\([0-9]*\) px (\([0-9.]*\)%).*/\2/p')
    [ -n "$ink" ] || { ink="ERR"; failures=$((failures + 1)); printf '%s\n' "$out" | tail -5; }
    [ -n "$over" ] || { over="ERR"; failures=$((failures + 1)); }
    printf '%-22s struct %6s%%  over-tol %6s%%\n' "$label" "$ink" "$over"
}
run idle
run focused-field   --focus='#name' --selection=11,17
run focused-area    --focus='#notes' --selection=2,14
run open-select     --open='#q'
run scrolled-list   --focus='#log' --scroll=40
# Hover was covered by nothing at all: 18 of the corpus stylesheets use it and
# every gate rendered them unhovered, so the Godot backend had never drawn a
# hovered frame. Three shapes, because they need different machinery -- the
# element itself, a descendant of a hovered ancestor, and a sibling of one.
run hovered-row     --hover='.entry'
run hovered-parent  --hover='.panel'
run hovered-sibling --hover='#name'
# The states this session added. Every one of them DRAWS something -- a dim
# behind a dialog, a popover in the top layer, a tooltip beside the cursor, a
# pressed button -- and none had ever been asked of the Godot backend. That is
# the gap that hid the hover bug, so it is closed for all of them at once.
run pressed         --press='#act'
run modal-dialog    --dialog='#ask'
run open-popover    --popover='#menu'
run tooltip         --tooltip='#act'
[ "$failures" -eq 0 ]
