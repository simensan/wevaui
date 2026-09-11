#!/usr/bin/env bash
# Renders every sample with the software backend and surveys each against
# Chrome over the cells that are flat in both. See chrome_survey.py for what
# the numbers mean and which false positives are expected.
#
# Usage: survey_all.sh <corpus-dir> [cell-size] [flatness]
set -u
CORPUS="${1:?usage: survey_all.sh <corpus-dir> [cell-size] [flatness]}"
CELL="${2:-16}"
FLAT="${3:-6}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RENDER="${WEVA_RENDER:-$HOME/weva/build-gcc/tools/weva_render/weva_render}"
DIAG="${WEVA_DIAG:-$HOME/weva/diag}"

for html in "$CORPUS"/*.html; do
    base="$(basename "$html" .html)"
    css="${html%.html}.css"
    [ -f "$css" ] || css="-"
    mkdir -p "$DIAG/$base"
    if ! "$RENDER" "$html" "$css" 1280 720 "$DIAG/$base/software.ppm" >/dev/null 2>&1; then
        echo "$base  RENDER FAILED"
        continue
    fi
    WEVA_DIAG="$DIAG" python3 "$ROOT/tools/oracle/chrome_survey.py" "$base" "$CELL" "$FLAT"
done
