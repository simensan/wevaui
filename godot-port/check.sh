#!/usr/bin/env bash
# Every gate this port has, in the order that fails fastest.
#
#   check.sh [--clean]
#
# The gates are not interchangeable and each catches what the others cannot:
#
#   unit tests          behaviour, at the C++ and C ABI level
#   sanitizers          the same tests again under ASan and UBSan, which have
#                       caught a double free, a premature free and a
#                       use-after-free in the element table this session alone
#   layout oracle       our layout against the C# reference, with Chrome
#                       arbitrating the disagreements, over all THREE corpora --
#                       samples, hand and harvest
#   backend gate        the software renderer against Godot's, on the IDENTICAL
#                       draw list, so a difference is the two rasterisers and
#                       nothing else
#   interactive gate    the same, in the states that only exist while a user is
#                       doing something: a cursor, a selection, a scrolled
#                       list, an open dropdown
#   host tests          the GDScript surface, driven from Godot
#   demo                the demo scene's own markup and script, so the
#                       explanation of how to use this cannot rot unnoticed
#
# Build directories are the ones the README sets up; anything missing is
# skipped with a line saying so, rather than passing quietly.
set -u

ROOT="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$ROOT/.." && pwd)"
GCC=${WEVA_BUILD_GCC:-$HOME/weva/build-gcc}
CLANG=${WEVA_BUILD_CLANG:-$HOME/weva/build-clang}
GODOT_BUILD=${WEVA_BUILD_GODOT:-$HOME/weva/build-godot}
GODOT=${GODOT_BIN:-$HOME/godot/godot}
SAMPLES="$ROOT/tools/oracle/corpus/samples"

failures=0
skipped=0

step() { printf '\n=== %s ===\n' "$1"; }
fail() { printf 'FAIL  %s\n' "$1"; failures=$((failures + 1)); }
skip() { printf 'skip  %s\n' "$1"; skipped=$((skipped + 1)); }

if [ "${1:-}" = "--clean" ]; then
    step "clean build"
    rm -rf "$GCC" "$CLANG" "$GODOT_BUILD"
fi

# ---- build ---------------------------------------------------------------
step "build"
if [ ! -f "$GCC/build.ninja" ]; then
    cmake -S "$ROOT" -B "$GCC" -G Ninja -DCMAKE_BUILD_TYPE=Release > /dev/null 2>&1
fi
if ! ( cd "$GCC" && ninja ) > /tmp/weva-build.log 2>&1; then
    fail "release build"
    grep -E "error:|FAILED" /tmp/weva-build.log | head -5
else
    echo "release ok"
fi

# ---- unit tests ----------------------------------------------------------
step "unit tests"
if [ -x "$GCC/libweva/tests/weva_tests" ]; then
    line=$("$GCC/libweva/tests/weva_tests" 2>&1 | tail -1)
    echo "$line"
    case "$line" in *", 0 failures"*) ;; *) fail "unit tests" ;; esac
else
    skip "unit tests (no binary)"
fi

# ---- sanitizers ----------------------------------------------------------
step "sanitizers"
if [ -f "$CLANG/build.ninja" ]; then
    if ! ( cd "$CLANG" && ninja ) > /tmp/weva-san-build.log 2>&1; then
        fail "sanitizer build"
        grep -E "error:|FAILED" /tmp/weva-san-build.log | head -5
    else
        line=$("$CLANG/libweva/tests/weva_tests" 2>&1 | tail -1)
        echo "$line"
        case "$line" in *", 0 failures"*) ;; *) fail "sanitizers" ;; esac
    fi
else
    skip "sanitizers (configure $CLANG with clang++ and -fsanitize=address,undefined)"
fi

# ---- layout, against the reference with Chrome arbitrating ---------------
#
# THREE corpora, not one. For a long time this gate ran only `samples`, and two
# real bugs lived comfortably underneath it: a subgrid growing implicit rows it
# should have clamped, and a list marker taking inline space so that every
# inline child of every <li> sat a marker-width too far right. Neither shows up
# in `samples` -- the marker one CANNOT, because a list item holding only text
# has no element after the marker for the dump to compare, and every item in
# every sample holds only text.
#
# `hand` is the small hand-written cases and `harvest` the ones lifted from the
# reference's own test suite. Both were sitting in the repo with a Chrome
# capture beside every case, and nothing ran them.
step "layout oracle"
if command -v python3 > /dev/null && [ -x "$GCC/tools/weva_dump/weva_dump" ]; then
    # corpus, width, height -> the summary line
    oracle() {
        (cd "$REPO" && python3 "$ROOT/tools/oracle/run_oracle.py" "$1" \
            --width "$2" --height "$3" --weva-dump "$GCC/tools/weva_dump/weva_dump" \
            --out-dir "/tmp/weva-oracle-$(basename "$1")" --quiet 2>&1 |
            grep "agree," | tail -1)
    }

    line=$(oracle "$SAMPLES" 1280 720)
    echo "samples:  ${line:-no result}"
    case "$line" in *" 0 differ,"*) ;; *) fail "layout oracle (samples)" ;; esac

    # The hand and harvest captures were taken at 800x600. Running them at 1280
    # reports most of the corpus differing on nothing but the width of some
    # full-width block, which is the first thing to check if this goes red.
    line=$(oracle "$ROOT/tools/oracle/corpus/hand" 800 600)
    echo "hand:     ${line:-no result}"
    case "$line" in *" 0 differ,"*) ;; *) fail "layout oracle (hand)" ;; esac

    # Harvest carries three cases that cannot pass yet, all waiting on the font
    # and form-control metrics decision in known-gaps/README.md: on those,
    # Chrome agrees with NEITHER engine. Gated at exactly three, so a fourth
    # breaks the build -- which is the part that matters.
    line=$(oracle "$ROOT/tools/oracle/corpus/harvest" 800 600)
    echo "harvest:  ${line:-no result}"
    case "$line" in
        *" 3 differ,"*) ;;
        *" 0 differ,"*|*" 1 differ,"*|*" 2 differ,"*)
            echo "  (fewer than the 3 known -- lower the number in check.sh)" ;;
        *) fail "layout oracle (harvest)" ;;
    esac
else
    skip "layout oracle (needs python3 and weva_dump)"
fi

# ---- cached property ids name real properties ----------------------------
#
# Reading a style by id is a quarter faster than by name, but an id is only
# equivalent for a REGISTERED property: an unregistered name resolves to
# kCustomPropertyId, and reading by that id finds nothing while reading by
# name finds it among the custom properties. Caching `list-style` -- a
# shorthand the registry does not know -- un-suppressed every marker in the
# corpus and moved four samples' box counts, and the only visible symptom was
# the oracle.
step "cached ids"
if command -v python3 > /dev/null; then
    line=$(python3 "$ROOT/tools/check_cached_ids.py" 2>&1 | tail -1)
    echo "${line:-no result}"
    case "$line" in "0 cached id"*) ;; *) fail "cached ids" ;; esac
else
    skip "cached ids (needs python3)"
fi

# ---- assets that silently drew nothing -----------------------------------
#
# An image that does not load is not an error anywhere: the box draws no
# background, no picture and no border image, and looks exactly like a box
# that has none -- and it agrees perfectly with any other engine failing the
# same way. That is how three separate tools shipped without a base path, each
# found only when somebody eventually looked at a picture.
#
# Reported rather than failed, because 9slice-demo legitimately names sprites
# that live in a Unity project rather than in this corpus. A NEW name in this
# list is a case that is not testing what it looks like it is testing.
step "assets"
if [ -x "$GCC/tools/weva_render/weva_render" ]; then
    missed=0
    for html in "$SAMPLES"/*.html; do
        css="${html%.html}.css"
        [ -f "$css" ] || css="-"
        names=$("$GCC/tools/weva_render/weva_render" "$html" "$css" 1280 720 /dev/null 2>&1                 >/dev/null | grep -v "did not load" || true)
        if [ -n "$names" ]; then
            printf '  %-22s %s
' "$(basename "$html" .html)"                 "$(printf '%s' "$names" | tr '
' ' ')"
            missed=$((missed + 1))
        fi
    done
    echo "$missed sample(s) reference an asset that did not load"
else
    skip "assets (needs weva_render)"
fi

# ---- the Godot extension -------------------------------------------------
#
# BEFORE the backend gate, which is the whole point of where this sits.
#
# That gate's premise is that both renderers get the IDENTICAL draw list, so
# any difference is the two rasterisers. It only holds if both are running the
# same engine. This build used to live down in the host tests, AFTER the gate,
# so every run that changed libweva compared a freshly built weva_render
# against a Godot still loading the PREVIOUS run's .so, and reported the
# version skew as a rasteriser difference. It failed on the first run and
# passed on the second, which is the most misleading way for a gate to behave.
step "godot extension"
if [ -x "$GODOT" ] && [ -f "$GODOT_BUILD/build.ninja" ]; then
    # The extension links straight into project/addons/weva/bin, so there is
    # nothing to copy afterwards. The `cp` that used to be here named a file
    # the build never writes and was silenced with `|| true` -- a good way to
    # hide a real staleness bug behind a no-op.
    if ( cd "$GODOT_BUILD" && ninja ) > /tmp/weva-host-build.log 2>&1; then
        echo "extension ok"
    else
        fail "godot host build"
        grep -E "error:|FAILED" /tmp/weva-host-build.log | head -5
    fi
else
    skip "godot extension (needs godot and a configured $GODOT_BUILD)"
fi

# ---- the two rasterisers, on the same draw list --------------------------
step "backend gate"
if [ -x "$GODOT" ] && [ -x "$GCC/tools/weva_render/weva_render" ]; then
    WEVA_RENDER="$GCC/tools/weva_render/weva_render" GODOT_BIN="$GODOT" \
        bash "$ROOT/hosts/godot/compare_all.sh" "$SAMPLES" > /tmp/weva-render.txt 2>&1
    worst=$(grep -c 'struct   [1-9]' /tmp/weva-render.txt || true)
    echo "$(wc -l < /tmp/weva-render.txt) samples, $worst over the structural gate"
    [ "$worst" = "0" ] || { fail "backend gate"; sort -t% -k1 -rn /tmp/weva-render.txt | head -3; }

    step "interactive gate"
    WEVA_RENDER="$GCC/tools/weva_render/weva_render" GODOT_BIN="$GODOT" \
        bash "$ROOT/hosts/godot/compare_live.sh" > /tmp/weva-live.txt 2>&1
    cat /tmp/weva-live.txt
    grep -q 'ERR' /tmp/weva-live.txt && fail "interactive gate"
    grep -qE 'struct +[1-9]' /tmp/weva-live.txt && fail "interactive gate"
    # The colour figure too, not only the structural one. A missing FILL is a
    # colour difference and barely moves the structural number: the hover bug
    # this gate was added to catch read 0.11% structural and 4.18% colour, so
    # the structural rule alone would have passed it. Every state is 0.00% on
    # both now, so anything reaching one percent is a real change.
    grep -qE 'over-tol +[1-9]' /tmp/weva-live.txt && fail "interactive gate"
else
    skip "backend gates (needs godot and weva_render)"
fi

# ---- the GDScript surface ------------------------------------------------
step "host tests"
if [ -x "$GODOT" ]; then
    # Built above, before the backend gate that depends on it.
    line=$(cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
        "$GODOT" --headless --path . test_scene.tscn 2>&1 | grep "godot host:" | tail -1)
    echo "${line:-no result}"
    case "$line" in *", 0 failures"*) ;; *) fail "host tests" ;; esac

    # The demo, driven through its OWN markup and script. It is the
    # explanation of how to use this from GDScript, and nothing else in the
    # suite reads it -- so without this it rots silently while every other
    # gate stays green.
    step "demo"
    line=$(cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
        "$GODOT" --headless --path . demo_smoke.tscn 2>&1 | grep "godot demo:" | tail -1)
    echo "${line:-no result}"
    case "$line" in *", 0 failures"*) ;; *) fail "demo" ;; esac

    # A whole screen, built the way someone would actually build one: a
    # gamepad-navigable grid, live data, a detail pane following the
    # selection, an equip action and a filter. It exists to find gaps in the
    # BINDING SURFACE that adding one method at a time never surfaces -- it
    # found three on its first run.
    step "inventory"
    line=$(cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300         "$GODOT" --headless --path . inventory_demo.tscn 2>&1 | grep "godot inventory:" | tail -1)
    echo "${line:-no result}"
    case "$line" in *", 0 failures"*) ;; *) fail "inventory" ;; esac

    # Two-way binding, from GDScript. The return path -- a `data-model`
    # control's value arriving back in the script's own dictionary -- is the
    # half no C++ test can reach, because the dictionary is Godot's.
    step "bindings"
    line=$(cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300         "$GODOT" --headless --path . binding_tests.tscn 2>&1 | grep "godot bindings:" | tail -1)
    echo "${line:-no result}"
    case "$line" in *", 0 failures"*) ;; *) fail "bindings" ;; esac

    # Hover, through the input path a WINDOW uses rather than set_pointer.
    # The interactive gate drives the engine in document coordinates, which is
    # the one path that cannot get a transform wrong -- so it would stay green
    # while a scaled, panned document in a Control hovered nothing.
    step "hover"
    line=$(cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300         "$GODOT" --headless --path . hover_tests.tscn 2>&1 | grep "godot hover:" | tail -1)
    echo "${line:-no result}"
    case "$line" in *", 0 failures"*) ;; *) fail "hover" ;; esac

    # And in the gallery itself, which is the scene a person looks at samples
    # in: a Node2D inside a clipped Control inside two containers.
    step "gallery hover"
    line=$(cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300         "$GODOT" --headless --path . gallery_hover_test.tscn 2>&1         | grep "godot gallery hover:" | tail -1)
    echo "${line:-no result}"
    case "$line" in *", 0 failures"*) ;; *) fail "gallery hover" ;; esac
else
    skip "host tests (no godot at $GODOT)"
fi

printf '\n'
if [ "$failures" -eq 0 ]; then
    printf 'all gates pass'
    [ "$skipped" -gt 0 ] && printf ' (%d skipped)' "$skipped"
    printf '\n'
    exit 0
fi
printf '%d gate(s) failed\n' "$failures"
exit 1
