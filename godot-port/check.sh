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
#                       arbitrating the disagreements
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
step "layout oracle"
if command -v python3 > /dev/null && [ -x "$GCC/tools/weva_dump/weva_dump" ]; then
    line=$(cd "$REPO" && python3 "$ROOT/tools/oracle/run_oracle.py" "$SAMPLES" \
        --width 1280 --height 720 --weva-dump "$GCC/tools/weva_dump/weva_dump" \
        --out-dir /tmp/weva-oracle --quiet 2>&1 | grep "agree," | tail -1)
    echo "${line:-no result}"
    case "$line" in *" 0 differ,"*) ;; *) fail "layout oracle" ;; esac
else
    skip "layout oracle (needs python3 and weva_dump)"
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
    if [ -f "$GODOT_BUILD/build.ninja" ]; then
        # The extension links straight into project/addons/weva/bin, so there
        # is nothing to copy afterwards. The `cp` that used to be here named a
        # file the build never writes and was silenced with `|| true` -- a good
        # way to hide a real staleness bug behind a no-op.
        ( cd "$GODOT_BUILD" && ninja ) > /tmp/weva-host-build.log 2>&1 ||
            { fail "godot host build"; grep -E "error:|FAILED" /tmp/weva-host-build.log | head -5; }
    fi
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
