#!/usr/bin/env bash
# One layout pass per corpus sample, worst first, as a MEDIAN of several sweeps.
#
# The median is the point. A single sweep of the corpus runs forty processes
# back to back and carries a spread of about +-3%, which is wide enough to
# invent a regression: the change that made box sides look themselves up by id
# read 1 to 8 per cent slower on its first sweep and consistently faster on the
# next three. Five consecutive runs of ONE page had shown +-0.5%, which is what
# made the single sweep look trustworthy. It was not.
#
#     Tools/layoutbench.sh [sweeps] [passes-per-sample]
#     Tools/layoutbench.sh --ab <bench-a> <bench-b> [sweeps] [passes]
#
# Use --ab for anything under about ten per cent. Running the tool before a
# change and again after does NOT work at that scale: the same binary measured
# twenty minutes apart read 3.052 ms and 3.262 on layout-stress, a seven per
# cent drift with no code between them, larger than most single optimisations.
# The median defends against variance WITHIN a sweep and nothing at all against
# drift BETWEEN invocations.
#
# --ab alternates the two binaries sample by sample inside one run, so both see
# the same machine, and prints them side by side with the delta.
set -euo pipefail

if [ "${1:-}" = "--ab" ]; then
    AB_A="${2:?usage: --ab <bench-a> <bench-b> [sweeps] [passes]}"
    AB_B="${3:?usage: --ab <bench-a> <bench-b> [sweeps] [passes]}"
    SWEEPS="${4:-5}"
    PASSES="${5:-40}"
else
    AB_A=""
    SWEEPS="${1:-3}"
    PASSES="${2:-40}"
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BENCH="${WEVA_BENCH:-${WEVA_BUILD_GCC:-$HOME/weva/build-gcc}/Tools/weva_bench/weva_bench}"
CORPUS="${WEVA_CORPUS:-$ROOT/Tools/oracle/corpus/samples}"

if ! [[ "$SWEEPS" =~ ^[1-9][0-9]*$ && "$PASSES" =~ ^[1-9][0-9]*$ ]]; then
    echo "sweeps and iterations must be positive integers" >&2
    exit 2
fi
shopt -s nullglob
pages=("$CORPUS"/*.html)
[ "${#pages[@]}" -gt 0 ] || { echo "no HTML pages in $CORPUS" >&2; exit 1; }
binaries=("$BENCH")
if [ -n "$AB_A" ]; then binaries=("$AB_A" "$AB_B"); fi
for binary in "${binaries[@]}"; do
    [ -x "$binary" ] || { echo "no executable benchmark at $binary" >&2; exit 1; }
done
tmp="$(mktemp -d)"
trap 'status=$?; if [ "$status" -eq 0 ]; then rm -rf "$tmp"; else printf "benchmark failed; evidence in %s\n" "$tmp" >&2; fi' EXIT

# The "best" figure for one sample through one binary, in milliseconds.
extract_best() {
    tail -1 | sed -n 's/.*best *\([0-9.]*\) ms.*/\1/p'
}

if [ -n "$AB_A" ]; then
    for sweep in $(seq 1 "$SWEEPS"); do
        for html in "${pages[@]}"; do
            base="$(basename "$html" .html)"
            css="${html%.html}.css"
            [ -f "$css" ] || css=""
            # Alternating, and in both orders across sweeps, so neither binary
            # sits permanently on the warmer or the colder side of the pair.
            if [ $((sweep % 2)) -eq 0 ]; then
                a=$("$AB_A" "$html" "$css" "$PASSES" 2>>"$tmp/errors.log" | extract_best)
                b=$("$AB_B" "$html" "$css" "$PASSES" 2>>"$tmp/errors.log" | extract_best)
            else
                b=$("$AB_B" "$html" "$css" "$PASSES" 2>>"$tmp/errors.log" | extract_best)
                a=$("$AB_A" "$html" "$css" "$PASSES" 2>>"$tmp/errors.log" | extract_best)
            fi
            [ -n "$a" ] && [ -n "$b" ] || { echo "missing benchmark metric for $base" >&2; exit 1; }
            echo "$base $a $b" >> "$tmp/ab"
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
        printf "%9s %9s %8s  %s\n", "A", "B", "B-A", "sample";
        for (s in as) {
            ma = median(as[s]); mb = median(bs[s]);
            printf "%9.3f %9.3f %7.1f%%  %s\n", ma, mb, (mb - ma) / ma * 100, s;
        }
    }' "$tmp/ab" | sort -k3 -rn
    exit 0
fi

for sweep in $(seq 1 "$SWEEPS"); do
    for html in "${pages[@]}"; do
        base="$(basename "$html" .html)"
        css="${html%.html}.css"
        [ -f "$css" ] || css=""
        line=$("$BENCH" "$html" "$css" "$PASSES" 2>>"$tmp/errors.log" | tail -1)
        best=$(printf '%s' "$line" | sed -n 's/.*best *\([0-9.]*\) ms.*/\1/p')
        boxes=$(printf '%s' "$line" | sed -n 's/.* \([0-9]*\) boxes.*/\1/p')
        allocs=$(printf '%s' "$line" | sed -n 's/.*allocations \([0-9]*\) .*/\1/p')
        [ -n "$best" ] || { echo "missing benchmark metric for $base" >&2; exit 1; }
        echo "$base $best ${boxes:-0} ${allocs:-0}" >> "$tmp/all"
    done
done

# Median per sample, so one unlucky sweep cannot move the answer.
awk '
{ times[$1] = times[$1] " " $2; boxes[$1] = $3; allocs[$1] = $4 }
END {
    for (s in times) {
        n = split(times[s], t, " ");
        for (i = 1; i <= n; i++)
            for (j = i + 1; j <= n; j++)
                if (t[j] + 0 < t[i] + 0) { swap = t[i]; t[i] = t[j]; t[j] = swap }
        med = (n % 2) ? t[(n + 1) / 2] : (t[n / 2] + t[n / 2 + 1]) / 2;
        printf "%9.3f %8d %8d  %s\n", med, boxes[s], allocs[s], s;
    }
}' "$tmp/all" | sort -rn
