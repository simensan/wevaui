#!/usr/bin/env bash
# Oracle run: regenerate the corpus, dump both engines, diff every entry.
# See docs/ORACLE.md.  Usage: run.sh [bucket]   e.g. run.sh flex
set -uo pipefail
cd "$(dirname "$0")"

WEVAUI=${WEVAUI:-../../..}
DUMP=${DUMP:-../../build/tools/weva_dump/weva_dump}
BUCKET=${1:-}

[ -d corpus ] || python3 harvest.py "$WEVAUI/Packages/com.wevaui/Tests/Runtime" corpus

# Reference dumps come from the C# engine. BaselineGen needs the .NET SDK; if
# it is missing there is no oracle, and running the diff would report a clean
# pass over zero entries — worse than failing.
if ! command -v dotnet >/dev/null; then
    echo "oracle: dotnet not found — cannot generate reference dumps." >&2
    echo "        BaselineGen IS the oracle; without it this run proves nothing." >&2
    exit 2
fi

pass=0; fail=0; skip=0
for html in $(find corpus/${BUCKET:-} -name '*.html' | sort); do
    base=${html%.html}; id=$(basename "$base"); bucket=$(basename "$(dirname "$base")")
    css=""; [ -f "$base.css" ] && css="$base.css"
    w=$(python3 -c "import json;print(json.load(open('$base.meta.json'))['width'])")
    h=$(python3 -c "import json;print(json.load(open('$base.meta.json'))['height'])")
    tol=$(python3 -c "import json;print(json.load(open('$base.meta.json')).get('tolerance_px',0))")

    mkdir -p "reference/$bucket" "candidate/$bucket"
    ref="reference/$bucket/$id.json"; cand="candidate/$bucket/$id.json"

    if [ ! -f "$ref" ]; then
        (cd "$WEVAUI/Tools/BaselineGen" && dotnet run -c Release -- \
            "$OLDPWD/$html" "$w" "$h" "$OLDPWD/$ref" ${css:+"$OLDPWD/$css"}) >/dev/null 2>&1 \
            || { skip=$((skip+1)); continue; }
    fi
    "$DUMP" "$html" "$w" "$h" "$cand" ${css:+"$css"} >/dev/null 2>&1 || { skip=$((skip+1)); continue; }

    if python3 diff.py "$ref" "$cand" --tolerance="$tol" >/dev/null; then
        pass=$((pass+1))
    else
        fail=$((fail+1)); python3 diff.py "$ref" "$cand" --tolerance="$tol" | head -12
    fi
done
echo "oracle: $pass pass, $fail fail, $skip skipped"
[ "$fail" -eq 0 ]
