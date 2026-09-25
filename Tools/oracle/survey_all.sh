#!/usr/bin/env bash
# Renders every sample with the software backend and surveys each against
# Chrome over the cells that are flat in both. See chrome_survey.py for what
# the numbers mean and which false positives are expected.
#
# Usage: survey_all.sh <corpus-dir> [cell-size] [flatness]
set -euo pipefail
CORPUS="${1:?usage: survey_all.sh <corpus-dir> [cell-size] [flatness]}"
CELL="${2:-16}"
FLAT="${3:-6}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RENDER="${WEVA_RENDER:-${WEVA_BUILD_GCC:-$HOME/weva/build-gcc}/Tools/weva_render/weva_render}"
DIAG="${WEVA_DIAG:-$HOME/weva/diag}"

shopt -s nullglob
pages=("$CORPUS"/*.html)
[ "${#pages[@]}" -gt 0 ] || { echo "no HTML pages in $CORPUS" >&2; exit 1; }
failures=0
for html in "${pages[@]}"; do
    base="$(basename "$html" .html)"
    css="${html%.html}.css"
    [ -f "$css" ] || css="-"
    mkdir -p "$DIAG/$base"
    if ! "$RENDER" "$html" "$css" 1280 720 "$DIAG/$base/software.ppm" >"$DIAG/$base/render.log" 2>&1; then
        echo "$base  RENDER FAILED; see $DIAG/$base/render.log"
        failures=$((failures + 1))
        continue
    fi
    if ! WEVA_CORPUS="$CORPUS" WEVA_DIAG="$DIAG" python3 "$ROOT/Tools/oracle/chrome_survey.py" "$base" "$CELL" "$FLAT"; then
        failures=$((failures + 1))
    fi
done
[ "$failures" -eq 0 ]
