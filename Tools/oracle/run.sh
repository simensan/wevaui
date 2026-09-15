#!/usr/bin/env bash
# The tracked corpus against Chrome. Usage: run.sh [hand|harvest|samples]
# DUMP overrides the standalone tool; WEVA_BUILD_GCC matches check.sh.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
GCC="${WEVA_BUILD_GCC:-$HOME/weva/build-gcc}"
DUMP="${DUMP:-$GCC/Tools/weva_dump/weva_dump}"
OUT="${WEVA_ORACLE_OUT:-/tmp/weva-oracle}"
if [ "$#" -gt 1 ]; then
    echo "Usage: run.sh [hand|harvest|samples]" >&2
    exit 2
fi
case "${1:-all}" in
    all) corpora=(hand harvest samples) ;;
    hand|harvest|samples) corpora=("$1") ;;
    *) echo "Unknown corpus: $1 (expected hand, harvest or samples)" >&2; exit 2 ;;
esac
failed=0
for corpus in "${corpora[@]}"; do
    width=800; height=600
    if [ "$corpus" = samples ]; then width=1280; height=720; fi
    python3 "$ROOT/Tools/oracle/run_oracle.py" "$ROOT/Tools/oracle/corpus/$corpus" \
        --weva-dump "$DUMP" --width "$width" --height "$height" \
        --out-dir "$OUT/$corpus" --known-gaps "$ROOT/Tools/oracle/known-gaps/chrome-sweep.txt" \
        || failed=1
done
exit "$failed"
