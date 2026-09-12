#!/usr/bin/env bash
# What ONE small change costs on a whole page — the incremental-layout gate.
#
# layoutbench.sh measures a layout from nothing, which is the cold case and the
# one the engine is already good at. This measures the warm case, which is what
# a running UI actually does: the document is laid out, one element changes, and
# the engine has to catch up. Today it catches up by rebuilding the box tree and
# laying out from the root, so the cost scales with the PAGE. Scoped to the
# dirty subtree it would scale with the CHANGE, and that is the difference this
# is built to see.
#
#     Tools/flipbench.sh [sweeps] [flips-per-sample]
#     Tools/flipbench.sh --ab <bench-a> <bench-b> [sweeps] [flips]
#
# Two flips per sample, alternating, so the engine cannot cache the answer:
#
#   layout   padding-left flipped between two values -> Invalidation::Layout
#   paint    background-color flipped                -> Invalidation::Paint
#
# The default target is the last element, a leaf. Set WEVA_FLIP_TARGET=*
# to measure a root change that necessarily invalidates the entire page.
#
# and the DIFFERENCE between them is what layout costs, with everything the two
# share -- cascade, paint, publishing the draw list -- subtracted out. That
# subtraction is the point: a paint flip repaints the whole page too, so the
# absolute numbers are dominated by paint and would hide a layout win entirely.
#
# Read `delta`, not `layout`. On layout-stress today it is about 2.8 ms for a
# padding change on one element of 6,926.
#
# The same median-of-sweeps discipline as layoutbench.sh, and for the same
# reason: a single sweep carries a spread wide enough to invent a regression.
# Use --ab for anything under about ten per cent.
set -u

if [ "${1:-}" = "--ab" ]; then
    AB_A="${2:?usage: --ab <bench-a> <bench-b> [sweeps] [flips]}"
    AB_B="${3:?usage: --ab <bench-a> <bench-b> [sweeps] [flips]}"
    SWEEPS="${4:-5}"
    FLIPS="${5:-40}"
else
    AB_A=""
    SWEEPS="${1:-3}"
    FLIPS="${2:-40}"
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BENCH="${WEVA_BENCH:-$HOME/weva/build-gcc/tools/weva_bench/weva_bench}"
CORPUS="${WEVA_CORPUS:-$ROOT/Tools/oracle/corpus/samples}"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# One flip mode through one binary, in milliseconds. `best` rather than mean:
# a flip allocates, and the mean carries whatever the allocator was doing.
run_flip() {   # binary, html, css, mode
    "$1" "$2" "$3" "$FLIPS" --full --dt=0 "--mutate=$4" "--target=${WEVA_FLIP_TARGET:-last}" 2>/dev/null |
        sed -n 's/.*best *\([0-9.]*\) ms.*/\1/p'
}

if [ -n "$AB_A" ]; then
    for sweep in $(seq 1 "$SWEEPS"); do
        for html in "$CORPUS"/*.html; do
            base="$(basename "$html" .html)"
            css="${html%.html}.css"
            [ -f "$css" ] || css=""
            # Alternating, and in both orders across sweeps, so neither binary
            # sits permanently on the warmer side of the pair.
            if [ $((sweep % 2)) -eq 0 ]; then
                al=$(run_flip "$AB_A" "$html" "$css" layout)
                ap=$(run_flip "$AB_A" "$html" "$css" paint)
                bl=$(run_flip "$AB_B" "$html" "$css" layout)
                bp=$(run_flip "$AB_B" "$html" "$css" paint)
            else
                bl=$(run_flip "$AB_B" "$html" "$css" layout)
                bp=$(run_flip "$AB_B" "$html" "$css" paint)
                al=$(run_flip "$AB_A" "$html" "$css" layout)
                ap=$(run_flip "$AB_A" "$html" "$css" paint)
            fi
            [ -n "$al" ] && [ -n "$ap" ] && [ -n "$bl" ] && [ -n "$bp" ] || continue
            echo "$base $(awk -v l="$al" -v p="$ap" 'BEGIN{print l-p}') $(awk -v l="$bl" -v p="$bp" 'BEGIN{print l-p}')" >> "$tmp/ab"
        done
    done
    awk '
    { as[$1] = as[$1] " " $2; bs[$1] = bs[$1] " " $3 }
    function median(list,   n, t, i, j, swap) {
        n = split(list, t, " ");
        for (i = 1; i <= n; i++)
            for (j = i + 1; j <= n; j++)
                if (t[j] + 0 < t[i] + 0) { swap = t[i]; t[i] = t[j]; t[j] = swap }
        return (n % 2) ? t[(n + 1) / 2] : (t[n / 2] + t[n / 2 + 1]) / 2;
    }
    END {
        printf "%9s %9s %8s  %s\n", "delta A", "delta B", "B-A", "sample";
        for (s in as) {
            ma = median(as[s]); mb = median(bs[s]);
            printf "%9.3f %9.3f %8.3f  %s\n", ma, mb, mb - ma, s;
        }
    }' "$tmp/ab" | sort -k3 -rn
    exit 0
fi

if [ ! -x "$BENCH" ]; then
    echo "no weva_bench at $BENCH (set WEVA_BENCH)" >&2
    exit 1
fi

for sweep in $(seq 1 "$SWEEPS"); do
    for html in "$CORPUS"/*.html; do
        base="$(basename "$html" .html)"
        css="${html%.html}.css"
        [ -f "$css" ] || css=""
        l=$(run_flip "$BENCH" "$html" "$css" layout)
        p=$(run_flip "$BENCH" "$html" "$css" paint)
        [ -n "$l" ] && [ -n "$p" ] || continue
        echo "$base $l $p" >> "$tmp/all"
    done
done

awk '
{ ls[$1] = ls[$1] " " $2; ps[$1] = ps[$1] " " $3 }
function median(list,   n, t, i, j, swap) {
    n = split(list, t, " ");
    for (i = 1; i <= n; i++)
        for (j = i + 1; j <= n; j++)
            if (t[j] + 0 < t[i] + 0) { swap = t[i]; t[i] = t[j]; t[j] = swap }
    return (n % 2) ? t[(n + 1) / 2] : (t[n / 2] + t[n / 2 + 1]) / 2;
}
END {
    printf "%9s %9s %9s  %s\n", "layout", "paint", "delta", "sample";
    for (s in ls) {
        ml = median(ls[s]); mp = median(ps[s]);
        printf "%9.3f %9.3f %9.3f  %s\n", ml, mp, ml - mp, s;
    }
}' "$tmp/all" | sort -k3 -rn
