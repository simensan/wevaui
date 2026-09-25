#!/usr/bin/env bash
# Every gate this port has, in the order that fails fastest.
#
#   check.sh [--clean] [--release]
#   --release makes any skipped gate a failure.
#
# The gates are not interchangeable and each catches what the others cannot:
#
#   unit tests          behaviour, at the C++ and C ABI level
#   sanitizers          the same tests again under ASan and UBSan, which have
#                       caught a double free, a premature free and a
#                       use-after-free in the element table this session alone
#   layout oracle       core layout against tracked Chrome captures over all
#                       three corpora: samples, hand and harvest
#   backend gate        the software renderer against Godot's, on the IDENTICAL
#                       draw list, so a difference is the two rasterisers and
#                       nothing else
#   interactive gate    the same, in the states that only exist while a user is
#                       doing something: a cursor, a selection, a scrolled
#                       list, an open dropdown
#   unity plugin        the same core as the Unity host's shared library: it
#                       builds, loads through the dynamic loader, and the
#                       generated P/Invoke layer matches the headers
#   host tests          the GDScript surface, driven from Godot
#   engine text safety  isolated long-Unicode shaping checks; stock Godot
#                       4.7.2 is known to fail and must not pass a release gate
#   native exports      installed addon in debug/release desktop games;
#                       requires templates matching GODOT_BIN. Set
#                       WEVA_EXPORT_RENDER=1 to also compare example pixels
#                       through a display with OpenGL 3 support.
#   demo                the demo scene's own markup and script, so the
#                       explanation of how to use this cannot rot unnoticed
#
# Build directories are the ones the README sets up; anything missing is
# skipped with a line saying so. Release mode requires every gate to run.
set -uo pipefail

# The engine and the repository are now the same directory: godot-port/ was
# collapsed into the root. ROOT and REPO are kept as separate names because
# 74 references below use one or the other, and the distinction may come
# back if the tree is ever split again.
ROOT="$(cd "$(dirname "$0")" && pwd)"
REPO="$ROOT"
GCC=${WEVA_BUILD_GCC:-$HOME/weva/build-gcc}
CLANG=${WEVA_BUILD_CLANG:-$HOME/weva/build-clang}
GODOT_BUILD=${WEVA_BUILD_GODOT:-$HOME/weva/build-godot}
UNITY_BUILD=${WEVA_BUILD_UNITY:-$HOME/weva/build-unity}
GODOT=${GODOT_BIN:-$HOME/godot/godot}
SAMPLES="$ROOT/Tools/oracle/corpus/samples"
# This run's logs and scratch output. Fixed /tmp names let concurrent runs
# overwrite each other's evidence and were predictable paths on a shared
# host; the directory is kept, since failed evidence is preserved.
LOGS=$(mktemp -d "${TMPDIR:-/tmp}/weva-gate.XXXXXX") || exit 1
printf 'logs: %s\n' "$LOGS"

failures=0
skipped=0
release=0
clean=0

for argument in "$@"; do
    case "$argument" in
        --release) release=1 ;;
        --clean) clean=1 ;;
        *) printf 'Unknown argument: %s\nUsage: check.sh [--clean] [--release]\n' "$argument" >&2; exit 2 ;;
    esac
done

template_args=()
chrome_check_args=()
if [ -n "${WEVA_CHROME_WINDOWS_PYTHON:-}" ] || [ -n "${WEVA_CHROME_WINDOWS:-}" ]; then
    if [ -z "${WEVA_CHROME_WINDOWS_PYTHON:-}" ] || [ -z "${WEVA_CHROME_WINDOWS:-}" ]; then
        printf 'Set both WEVA_CHROME_WINDOWS_PYTHON and WEVA_CHROME_WINDOWS for the Windows reference.\n' >&2
        exit 2
    fi
    chrome_check_args=(--windows-python "$WEVA_CHROME_WINDOWS_PYTHON" --chrome "$WEVA_CHROME_WINDOWS")
fi
if [ -n "${WEVA_GODOT_DEBUG_TEMPLATE:-}" ] || [ -n "${WEVA_GODOT_RELEASE_TEMPLATE:-}" ]; then
    if [ -z "${WEVA_GODOT_DEBUG_TEMPLATE:-}" ] || [ -z "${WEVA_GODOT_RELEASE_TEMPLATE:-}" ]; then
        printf 'Set WEVA_GODOT_DEBUG_TEMPLATE and WEVA_GODOT_RELEASE_TEMPLATE together.\n' >&2
        exit 2
    fi
    template_args=(--debug-template "$WEVA_GODOT_DEBUG_TEMPLATE" --release-template "$WEVA_GODOT_RELEASE_TEMPLATE")
fi

step() { printf '\n=== %s ===\n' "$1"; }
fail() { printf 'FAIL  %s\n' "$1"; failures=$((failures + 1)); }
skip() { printf 'skip  %s\n' "$1"; skipped=$((skipped + 1)); }

if [ "$clean" -eq 1 ]; then
    step "clean build"
    # Preserve configured dependencies and sanitizer settings. Never recursively
    # delete an environment-supplied path (which could be a source directory).
    for build in "$GCC" "$CLANG" "$GODOT_BUILD"; do
        if [ -f "$build/CMakeCache.txt" ]; then
            cmake --build "$build" --target clean || exit 1
        fi
    done
fi

# ---- build ---------------------------------------------------------------
step "release, sample and text-safety verification tools"
python3 -m unittest discover -s "$ROOT/Tools/tests" \
    || fail "release/sample verification tools"
python3 -m unittest discover -s "$ROOT/Tools/godot-text-shaping-repro" -p 'test_*.py' \
    || fail "text-safety verification tools"

step "performance verification tools"
python3 -m unittest discover -s "$ROOT/hosts/godot" -p 'test_frontier_perf*.py' \
    || fail "performance verification tools"

step "build"
if [ ! -f "$GCC/build.ninja" ]; then
    if ! cmake -S "$ROOT" -B "$GCC" -G Ninja -DCMAKE_BUILD_TYPE=Release \
            > $LOGS/configure.log 2>&1; then
        cat $LOGS/configure.log
        exit 1
    fi
fi
if ! ( cd "$GCC" && ninja ) > $LOGS/build.log 2>&1; then
    fail "release build"
    grep -E "error:|FAILED" $LOGS/build.log | head -5
    exit 1 # A stale binary must not supply evidence after a failed build.
else
    echo "release ok"
fi

# ---- complete core suite -------------------------------------------------
# CTest owns the suite list, including the benchmark CLI and allocation guards.
# Check process exits, not a trailing success string from a crashed process.
step "core tests and incremental corpus"
if WEVA_INCREMENTAL_CORPUS="$SAMPLES" ctest --test-dir "$GCC" \
        --output-on-failure --no-tests=error > $LOGS/core.log 2>&1; then
    cat $LOGS/core.log
else
    fail "core tests and incremental corpus"
    tail -45 $LOGS/core.log
fi

# ---- sanitizers ----------------------------------------------------------
step "sanitizers"
if [ -f "$CLANG/CMakeCache.txt" ]; then
    # Configure the actual instrumentation and positive controls. Merely
    # naming a directory "build-clang" does not prove sanitizers are active.
    if ! cmake -S "$ROOT" -B "$CLANG" -DWEVA_SANITIZERS=ON -DWEVA_BUILD_TESTS=ON \
            > $LOGS/san-build.log 2>&1 ||
       ! cmake --build "$CLANG" >> $LOGS/san-build.log 2>&1; then
        fail "sanitizer configure/build"
        tail -20 $LOGS/san-build.log
    else
        # Include retained/full sample mutations, allocation guards and CLI
        # checks, and require the expected ASan/UBSan positive-control reports.
        if WEVA_INCREMENTAL_CORPUS="$SAMPLES" ctest --test-dir "$CLANG" \
                --output-on-failure --no-tests=error > $LOGS/sanitizers.log 2>&1; then
            cat $LOGS/sanitizers.log
        else
            fail "sanitizers"
            tail -45 $LOGS/sanitizers.log
        fi
    fi
else
    skip "sanitizers (configure $CLANG with clang++ and -DWEVA_SANITIZERS=ON)"
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
step "chrome oracle"
# Chrome is the only oracle (2026-09-13): each corpus case is laid out by the
# core and compared with the Chrome capture tracked beside it. A case passes
# when its worst value is within 1.5px of Chrome and every element pairs;
# anything over the ceiling must be named in known-gaps/chrome-sweep.txt with
# a cause, and the gate reports entries there that are no longer needed.
#
# The ceiling is measured, not chosen: the harvested tests' worst disagreement
# is 1.0px, the samples' text-baseline rounding tops out at 1.3px, and the
# first genuine divergence in any corpus is 3.8px. This replaced the three-way
# run_oracle.py gate, whose C# reference leg is retired with the C# engine.
if command -v python3 > /dev/null && [ -x "$GCC/Tools/weva_dump/weva_dump" ]; then
    chrome_oracle() {
        local corpus="$1" width="$2" height="$3"
        local log="$LOGS/chrome-$(basename "$corpus").log"
        if ! (cd "$REPO" && python3 "$ROOT/Tools/oracle/chrome_sweep.py" "$corpus" \
                --width "$width" --height "$height" \
                --weva-dump "$GCC/Tools/weva_dump/weva_dump" \
                --out-dir "$LOGS/chrome-$(basename "$corpus")" --show 2 --chrome-metrics \
                --max-worst 1.5 --known-gaps "$ROOT/Tools/oracle/known-gaps/chrome-sweep.txt") \
                > "$log" 2>&1; then
            fail "chrome oracle ($(basename "$corpus")); see $log"
        fi
    }
    chrome_oracle "$SAMPLES" 1280 720
    chrome_oracle "$ROOT/Tools/oracle/corpus/hand" 800 600
    chrome_oracle "$ROOT/Tools/oracle/corpus/harvest" 800 600
else
    fail "chrome oracle: needs python3 and $GCC/Tools/weva_dump/weva_dump (a gate that cannot run is red, not skipped)"
fi

step "chrome behaviour checks"
# What a capture cannot pin -- focus order, popover chains, number stepping,
# table border junctions, selection, animation clocks -- the scripts drive a
# real Chrome through puppeteer and compare. Their answers are already frozen
# into the core suite; running them says whether Chrome still gives them.
# Needs node, puppeteer (repo-root node_modules) and a launchable Chrome.
# WEVA_NO_CHROME=1 opts out explicitly; a missing tool is otherwise a failure,
# because a gate that silently cannot run is how the layout oracle skipped
# for a week without anyone noticing.
if [ -n "${WEVA_NO_CHROME:-}" ]; then
    skip "chrome behaviour checks (WEVA_NO_CHROME set)"
elif command -v node > /dev/null && command -v python3 > /dev/null; then
    # A Windows-mounted output also lets WSL launch the canonical Windows
    # reference. Keep receipts in the checkout instead of ephemeral /tmp.
    if ! python3 "$ROOT/Tools/oracle/run_chrome_checks.py" \
            --out "${WEVA_CHROME_CHECKS_OUT:-$ROOT/.utmp/chrome-checks}" "${chrome_check_args[@]}" \
            > $LOGS/chrome-checks.log 2>&1; then
        fail "chrome behaviour checks; see $LOGS/chrome-checks.log"
    fi
else
    fail "chrome behaviour checks: needs node and python3 (set WEVA_NO_CHROME=1 to opt out)"
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
    if python3 "$ROOT/Tools/check_cached_ids.py" > $LOGS/cached-ids.log 2>&1; then
        tail -1 $LOGS/cached-ids.log
    else
        fail "cached ids"
        cat $LOGS/cached-ids.log
    fi
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
# Every image in the sample corpus must be present and decodable. Otherwise
# matching empty boxes can conceal a broken sample on both sides of the oracle.
step "assets"
if [ -x "$GCC/Tools/weva_render/weva_render" ]; then
    missed=0
    for html in "$SAMPLES"/*.html; do
        css="${html%.html}.css"
        [ -f "$css" ] || css="-"
        if names=$("$GCC/Tools/weva_render/weva_render" "$html" "$css" 1280 720 /dev/null 2>&1 >/dev/null); then
            [ -z "$names" ] && continue
        fi
        printf '  %s: %s\n' "$(basename "$html" .html)" "$names"
        missed=$((missed + 1))
    done
    if [ "$missed" -eq 0 ]; then
        echo "all sample assets loaded"
    else
        fail "$missed sample(s) failed to render or reference missing assets"
    fi
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
step "unity plugin"
# The same core as the Unity host's shared library: built, opened through the
# dynamic loader by the host-free load test, and the generated P/Invoke layer
# compared against the headers. The Unity editor itself is not part of this
# script; its EditMode round trip is recorded in
# docs/verification/unity-host-prototype.json.
if [ ! -f "$UNITY_BUILD/build.ninja" ]; then
    if ! cmake -S "$ROOT/hosts/unity" -B "$UNITY_BUILD" -G Ninja -DCMAKE_BUILD_TYPE=Release \
            -DWEVA_UNITY_BIN="$UNITY_BUILD/bin" > $LOGS/unity-configure.log 2>&1; then
        fail "unity plugin configure"
        tail -20 $LOGS/unity-configure.log
    fi
fi
if [ -f "$UNITY_BUILD/build.ninja" ]; then
    if ! ( cd "$UNITY_BUILD" && ninja ) > $LOGS/unity-build.log 2>&1; then
        fail "unity plugin build"
        grep -E "error:|FAILED" $LOGS/unity-build.log | head -5
    elif ! "$UNITY_BUILD/weva_core_load_test" "$UNITY_BUILD/bin/weva_core.so"; then
        fail "unity plugin load test"
    elif ! python3 "$ROOT/hosts/unity/gen_bindings.py" --header "$ROOT/libweva/include/weva_c.h" \
            --header "$ROOT/hosts/unity/src/weva_unity.h" \
            --out "$REPO/Packages/com.wevaui/Runtime/Native/WevaNative.g.cs" --check; then
        fail "unity bindings drift"
    else
        echo "unity plugin ok"
    fi
fi

step "godot extension"
if [ -x "$GODOT" ] && [ -f "$GODOT_BUILD/build.ninja" ]; then
    # The extension links straight into project/addons/weva/bin, so there is
    # nothing to copy afterwards. The `cp` that used to be here named a file
    # the build never writes and was silenced with `|| true` -- a good way to
    # hide a real staleness bug behind a no-op.
    if ( cd "$GODOT_BUILD" && ninja ) > $LOGS/host-build.log 2>&1; then
        echo "extension ok"
    else
        fail "godot host build"
        grep -E "error:|FAILED" $LOGS/host-build.log | head -5
        exit 1 # Do not package or verify the previously installed extension.
    fi
else
    skip "godot extension (needs godot and a configured $GODOT_BUILD)"
    # Every later Godot gate but the engine's own text safety loads the addon,
    # and without this build they would load whatever libweva_godot.so is
    # already in project/addons/weva/bin -- a stale extension passing for the
    # current source. They skip instead.
    godot_engine="$GODOT"
    GODOT=""
fi
godot_engine="${godot_engine:-$GODOT}"

# ---- engine text safety, independent of the addon and its test font ------
# Ordinary interaction fixtures avoid the known long-emoji engine crash.
# Their success therefore cannot establish safety for unrestricted input.
step "Godot engine text safety"
if [ -x "$godot_engine" ] && command -v python3 > /dev/null; then
    shaping_temporary=$(mktemp -d) || exit 1
    if python3 "$ROOT/Tools/godot-text-shaping-repro/check.py" --godot "$godot_engine" \
            --logs "$shaping_temporary/results" > "$shaping_temporary/check.log" 2>&1; then
        cat "$shaping_temporary/check.log"
    else
        fail "Godot engine text safety"
        cat "$shaping_temporary/check.log"
    fi
    # Keep engine identity, process exits and unfiltered logs on failure too.
    printf 'Text safety artifacts: %s\n' "$shaping_temporary"
else
    skip "Godot engine text safety (needs Godot and python3)"
fi

# ---- actual templates, with ICU embedded rather than bypassed ------------
step "Godot exported Unicode safety"
if [ -x "$GODOT" ] && command -v python3 > /dev/null; then
    unicode_export_temporary=$(mktemp -d) || exit 1
    if python3 "$ROOT/Tools/godot-text-shaping-repro/check_exports.py" --godot "$GODOT" \
            "${template_args[@]}" --logs "$unicode_export_temporary/results" \
            > "$unicode_export_temporary/check.log" 2>&1; then
        cat "$unicode_export_temporary/check.log"
    else
        fail "Godot exported Unicode safety"
        cat "$unicode_export_temporary/check.log"
    fi
    printf 'Exported Unicode artifacts: %s\n' "$unicode_export_temporary"
else
    skip "Godot exported Unicode safety (needs Godot editor, export templates and python3)"
fi

# ---- native font shaping, independent of the stub-font backend gate ------
step "Godot font adapter"
font_test_library=${WEVA_GODOT_FONT_TEST_LIBRARY:-$ROOT/hosts/godot/project/addons/weva/bin/libweva_font_tests.so}
if [ -x "$GODOT" ] && command -v python3 > /dev/null && [ -f "$font_test_library" ]; then
    if python3 "$ROOT/hosts/godot/check_font_shaping.py" --godot "$GODOT" \
            --library "$font_test_library" > $LOGS/font-shaping.log 2>&1; then
        cat $LOGS/font-shaping.log
    else
        fail "Godot font adapter"
        tail -25 $LOGS/font-shaping.log
    fi
else
    skip "Godot font adapter (configure host with -DWEVA_GODOT_FONT_TESTS=ON)"
fi

# ---- install a packaged addon and run its exported resources -------------
step "Godot addon installation and native exports"
if [ -x "$GODOT" ] && command -v python3 > /dev/null && [ -f "$GODOT_BUILD/CMakeCache.txt" ]; then
    godot_cpp_source=$(sed -n 's/^GODOT_CPP_DIR:[^=]*=//p' "$GODOT_BUILD/CMakeCache.txt")
    addon_temporary=$(mktemp -d) || exit 1
    export_checks=(--native)
    if [ "$release" -eq 1 ] && [ "${WEVA_EXPORT_RENDER:-0}" != 1 ]; then
        skip "native export pixels (set WEVA_EXPORT_RENDER=1 with a display)"
    fi
    if [ "${WEVA_EXPORT_RENDER:-0}" = 1 ]; then export_checks+=(--render); fi
    if python3 "$ROOT/hosts/godot/package_addon.py" \
            --linux-library "$ROOT/hosts/godot/project/addons/weva/bin/libweva_godot.so" \
            --godot-cpp-dir "$godot_cpp_source" --version "${WEVA_PACKAGE_VERSION:-0.1.0-preview.0}" \
            --output "$addon_temporary/weva-addon.zip" > $LOGS/export.log 2>&1 && \
       python3 "$ROOT/hosts/godot/check_export.py" --godot "$GODOT" \
            --addon "$addon_temporary/weva-addon.zip" "${export_checks[@]}" "${template_args[@]}" >> $LOGS/export.log 2>&1; then
        cat $LOGS/export.log
    else
        fail "Godot addon installation and native exports"
        tail -25 $LOGS/export.log
    fi
    printf 'Addon artifacts: %s\n' "$addon_temporary"
else
    skip "Godot addon installation and native exports (needs Godot editor, python3 and configured host)"
fi

# ---- the two rasterisers, on the same draw list --------------------------
step "backend gate"
if [ -x "$GODOT" ] && [ -x "$GCC/Tools/weva_render/weva_render" ]; then
    if ! WEVA_RENDER="$GCC/Tools/weva_render/weva_render" GODOT_BIN="$GODOT" \
        bash "$ROOT/hosts/godot/compare_all.sh" "$SAMPLES" > $LOGS/render.txt 2>&1; then
        fail "backend comparison process"
        tail -15 $LOGS/render.txt
    fi
    worst=$(grep -c 'struct   [1-9]' $LOGS/render.txt || true)
    echo "$(wc -l < $LOGS/render.txt) samples, $worst over the structural gate"
    [ "$worst" = "0" ] || { fail "backend gate"; sort -t% -k1 -rn $LOGS/render.txt | head -3; }

    step "interactive gate"
    if ! WEVA_RENDER="$GCC/Tools/weva_render/weva_render" GODOT_BIN="$GODOT" \
        bash "$ROOT/hosts/godot/compare_live.sh" > $LOGS/live.txt 2>&1; then
        fail "interactive comparison process"
    fi
    cat $LOGS/live.txt
    grep -q 'ERR' $LOGS/live.txt && fail "interactive gate"
    grep -qE 'struct +[1-9]' $LOGS/live.txt && fail "interactive gate"
    # The colour figure too, not only the structural one. A missing FILL is a
    # colour difference and barely moves the structural number: the hover bug
    # this gate was added to catch read 0.11% structural and 4.18% colour, so
    # the structural rule alone would have passed it. Every state is 0.00% on
    # both now, so anything reaching one percent is a real change.
    grep -qE 'over-tol +[1-9]' $LOGS/live.txt && fail "interactive gate"
    step "triangle batching pixels"
    if python3 "$ROOT/hosts/godot/check_triangle_batching.py" --godot "$GODOT" \
            --project "$ROOT/hosts/godot/project" > $LOGS/triangle-batching.log 2>&1; then
        grep 'triangle batching: 6 identical images' $LOGS/triangle-batching.log
    else
        fail "triangle batching pixels"
        tail -25 $LOGS/triangle-batching.log
    fi
    step "packed draw cache pixels"
    if python3 "$ROOT/hosts/godot/check_packed_draws.py" --godot "$GODOT" \
            --project "$ROOT/hosts/godot/project" > $LOGS/packed-draws.log 2>&1; then
        grep 'identical images' $LOGS/packed-draws.log
    else
        fail "packed draw cache pixels"
        tail -25 $LOGS/packed-draws.log
    fi
    step "glyph preparation cache pixels"
    if python3 "$ROOT/hosts/godot/check_packed_draws.py" --godot "$GODOT" \
            --project "$ROOT/hosts/godot/project" --cache-kind glyphs > $LOGS/glyph-reuse.log 2>&1; then
        grep 'identical images' $LOGS/glyph-reuse.log
    else
        fail "glyph preparation cache pixels"
        tail -25 $LOGS/glyph-reuse.log
    fi
else
    skip "backend gates (needs godot and weva_render)"
fi

# One scene suite: its summary line must report no failures, and Godot must
# exit cleanly with no script error anywhere in the log. Grepping only the
# summary passed a scene that crashed or errored after printing it.
scene_step() {
    local name="$1" scene="$2" summary="$3"
    local log="$LOGS/scene-${scene%.tscn}.log"
    step "$name"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . "$scene" ) > "$log" 2>&1 &&
       grep "$summary" "$log" | tail -1 | grep -q ', 0 failures' &&
       ! grep -q 'SCRIPT ERROR:' "$log"; then
        grep "$summary" "$log" | tail -1
    else
        fail "$name"
        tail -25 "$log"
    fi
}

# ---- the GDScript surface ------------------------------------------------
step "host tests"
if [ -x "$GODOT" ]; then
    # Built above, before the backend gate that depends on it.
    if (cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
        "$GODOT" --headless --path . test_scene.tscn) > $LOGS/host.log 2>&1 &&
       grep -q 'godot host:.* checks, 0 failures' $LOGS/host.log &&
       ! grep -qE 'ERROR:|^FAIL' $LOGS/host.log; then
        grep 'godot host:' $LOGS/host.log
    else
        fail "host tests"
        tail -25 $LOGS/host.log
    fi

    # Native routing is distinct from the direct ABI/input methods exercised
    # by the host suite. Keep focus, stacking and Control sizing in the gate.
    step "native input integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . input_integration_tests.tscn ) > $LOGS/input.log 2>&1 &&
       grep -q 'godot input integration:.* 0 failures' $LOGS/input.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/input.log; then
        grep 'godot input integration:' $LOGS/input.log
    else
        fail "native input integration"
        tail -25 $LOGS/input.log
    fi

    step "keyboard integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . keyboard_integration_tests.tscn ) > $LOGS/keyboard.log 2>&1 &&
       grep -q 'godot keyboard integration:.* 0 failures' $LOGS/keyboard.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/keyboard.log; then
        grep 'godot keyboard integration:' $LOGS/keyboard.log
    else
        fail "keyboard integration"
        tail -25 $LOGS/keyboard.log
    fi

    step "IME integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . ime_integration_tests.tscn ) > $LOGS/ime.log 2>&1 &&
       grep -q 'godot IME integration:.* 0 failures' $LOGS/ime.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/ime.log; then
        grep 'godot IME integration:' $LOGS/ime.log
    else
        fail "IME integration"
        tail -25 $LOGS/ime.log
    fi

    step "theme font integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . theme_font_tests.tscn ) > $LOGS/theme-font.log 2>&1 &&
       grep -q 'godot theme fonts:.* 0 failures' $LOGS/theme-font.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/theme-font.log; then
        grep 'godot theme fonts:' $LOGS/theme-font.log
    else
        fail "theme font integration"
        tail -25 $LOGS/theme-font.log
    fi

    step "font face integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . font_face_tests.tscn ) > $LOGS/font-face.log 2>&1 &&
       grep -q 'godot font face:.* 0 failures' $LOGS/font-face.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/font-face.log; then
        grep 'godot font face:' $LOGS/font-face.log
    else
        fail "font face integration"
        tail -25 $LOGS/font-face.log
    fi

    step "bidi integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . bidi_tests.tscn ) > $LOGS/bidi.log 2>&1 &&
       grep -q 'godot bidi:.* 0 failures' $LOGS/bidi.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/bidi.log; then
        grep 'godot bidi:' $LOGS/bidi.log
    else
        fail "bidi integration"
        tail -25 $LOGS/bidi.log
    fi

    step "gamepad navigation integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . gamepad_navigation_tests.tscn ) > $LOGS/gamepad.log 2>&1 &&
       grep -q 'godot gamepad navigation:.* 0 failures' $LOGS/gamepad.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/gamepad.log; then
        grep 'godot gamepad navigation:' $LOGS/gamepad.log
    else
        fail "gamepad navigation integration"
        tail -25 $LOGS/gamepad.log
    fi

    step "live reload integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . live_reload_tests.tscn ) > $LOGS/live-reload.log 2>&1 &&
       grep -q 'godot live reload:.* 0 failures' $LOGS/live-reload.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/live-reload.log; then
        grep 'godot live reload:' $LOGS/live-reload.log
    else
        fail "live reload integration"
        tail -25 $LOGS/live-reload.log
    fi

    step "on-screen keyboard integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . onscreen_keyboard_tests.tscn ) > $LOGS/onscreen-keyboard.log 2>&1 &&
       grep -q 'godot on-screen keyboard:.* 0 failures' $LOGS/onscreen-keyboard.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/onscreen-keyboard.log; then
        grep 'godot on-screen keyboard:' $LOGS/onscreen-keyboard.log
    else
        fail "on-screen keyboard integration"
        tail -25 $LOGS/onscreen-keyboard.log
    fi

    step "line height integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . line_height_tests.tscn ) > $LOGS/line-height.log 2>&1 &&
       grep -q 'godot line height:.* 0 failures' $LOGS/line-height.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/line-height.log; then
        grep 'godot line height:' $LOGS/line-height.log
    else
        fail "line height integration"
        tail -25 $LOGS/line-height.log
    fi

    step "font inheritance integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . font_inheritance_tests.tscn ) > $LOGS/font-inheritance.log 2>&1 &&
       grep -q 'godot font inheritance:.* 0 failures' $LOGS/font-inheritance.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/font-inheritance.log; then
        grep 'godot font inheritance:' $LOGS/font-inheritance.log
    else
        fail "font inheritance integration"
        tail -25 $LOGS/font-inheritance.log
    fi

    step "font-size context integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . font_size_tests.tscn ) > $LOGS/font-size.log 2>&1 &&
       grep -q 'godot font-size context:.* 0 failures' $LOGS/font-size.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/font-size.log; then
        grep 'godot font-size context:' $LOGS/font-size.log
    else
        fail "font-size context integration"
        tail -25 $LOGS/font-size.log
    fi

    step "intrinsic sizing integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . intrinsic_size_tests.tscn ) > $LOGS/intrinsic-size.log 2>&1 &&
       grep -q 'godot intrinsic sizing: 336 checks, 0 failures' $LOGS/intrinsic-size.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/intrinsic-size.log; then
        grep 'godot intrinsic sizing:' $LOGS/intrinsic-size.log
    else
        fail "intrinsic sizing integration"
        tail -25 $LOGS/intrinsic-size.log
    fi

    step "text editing integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . text_editing_tests.tscn ) > $LOGS/text-editing.log 2>&1 &&
       grep -q 'godot text editing:.* 0 failures' $LOGS/text-editing.log &&
       ! grep -qE 'ERROR:|Unicode parsing error|FAIL ' $LOGS/text-editing.log; then
        grep 'godot text editing:' $LOGS/text-editing.log
    else
        fail "text editing integration"
        tail -25 $LOGS/text-editing.log
    fi

    step "text selection autoscroll integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . text_autoscroll_tests.tscn ) > $LOGS/text-autoscroll.log 2>&1 &&
       grep -q 'godot text autoscroll:.* 0 failures' $LOGS/text-autoscroll.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/text-autoscroll.log; then
        grep 'godot text autoscroll:' $LOGS/text-autoscroll.log
    else
        fail "text selection autoscroll integration"
        tail -25 $LOGS/text-autoscroll.log
    fi

    step "maxlength and paste integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . maxlength_tests.tscn ) > $LOGS/maxlength.log 2>&1 &&
       grep -q 'godot maxlength:.* 0 failures' $LOGS/maxlength.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/maxlength.log; then
        grep 'godot maxlength:' $LOGS/maxlength.log
    else
        fail "maxlength and paste integration"
        tail -25 $LOGS/maxlength.log
    fi

    step "form state and reset integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . form_state_tests.tscn ) > $LOGS/form-state.log 2>&1 &&
       grep -q 'godot form state:.* 0 failures' $LOGS/form-state.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/form-state.log; then
        grep 'godot form state:' $LOGS/form-state.log
    else
        fail "form state and reset integration"
        tail -25 $LOGS/form-state.log
    fi

    step "dialog cancellation and result integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . dialog_cancel_tests.tscn ) > $LOGS/dialog-cancel.log 2>&1 &&
       grep -q 'godot dialog cancellation:.* 0 failures' $LOGS/dialog-cancel.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/dialog-cancel.log; then
        grep 'godot dialog cancellation:' $LOGS/dialog-cancel.log
    else
        fail "dialog cancellation and result integration"
        tail -25 $LOGS/dialog-cancel.log
    fi

    step "form validation integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . form_validation_tests.tscn ) > $LOGS/form-validation.log 2>&1 &&
       grep -q 'godot form validation:.* 0 failures' $LOGS/form-validation.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/form-validation.log; then
        grep 'godot form validation:' $LOGS/form-validation.log
    else
        fail "form validation integration"
        tail -25 $LOGS/form-validation.log
    fi

    step "validity selector integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . validity_selector_tests.tscn ) > $LOGS/validity-selectors.log 2>&1 &&
       grep -q 'godot validity selectors:.* 0 failures' $LOGS/validity-selectors.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/validity-selectors.log; then
        grep 'godot validity selectors:' $LOGS/validity-selectors.log
    else
        fail "validity selector integration"
        tail -25 $LOGS/validity-selectors.log
    fi

    step "multicol sizing integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . multicol_sizing_tests.tscn ) > $LOGS/multicol-sizing.log 2>&1 &&
       grep -q 'godot multicol sizing:.* 0 failures' $LOGS/multicol-sizing.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/multicol-sizing.log; then
        grep 'godot multicol sizing:' $LOGS/multicol-sizing.log
    else
        fail "multicol sizing integration"
        tail -25 $LOGS/multicol-sizing.log
    fi

    step "block margin integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . block_margin_tests.tscn ) > $LOGS/block-margins.log 2>&1 &&
       grep -q 'godot block margins:.* 0 failures' $LOGS/block-margins.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/block-margins.log; then
        grep 'godot block margins:' $LOGS/block-margins.log
    else
        fail "block margin integration"
        tail -25 $LOGS/block-margins.log
    fi

    step "cumulative timing integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . timing_tests.tscn ) > $LOGS/timing.log 2>&1 &&
       grep -q 'godot timing:.* 0 failures' $LOGS/timing.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/timing.log; then
        grep 'godot timing:' $LOGS/timing.log
    else
        fail "cumulative timing integration"
        tail -25 $LOGS/timing.log
    fi

    step "input geometry integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . input_geometry_tests.tscn ) > $LOGS/input-geometry.log 2>&1 &&
       grep -q 'godot input geometry:.* 0 failures' $LOGS/input-geometry.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/input-geometry.log; then
        grep 'godot input geometry:' $LOGS/input-geometry.log
    else
        fail "input geometry integration"
        tail -25 $LOGS/input-geometry.log
    fi

    step "range direction integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . range_direction_tests.tscn ) > $LOGS/range-direction.log 2>&1 &&
       grep -q 'range direction:.* 0 failures' $LOGS/range-direction.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/range-direction.log; then
        grep 'range direction:' $LOGS/range-direction.log
    else
        fail "range direction integration"
        tail -25 $LOGS/range-direction.log
    fi

    step "number keyboard integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . number_step_tests.tscn ) > $LOGS/number-steps.log 2>&1 &&
       grep -q 'number steps:.* 0 failures' $LOGS/number-steps.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/number-steps.log; then
        grep 'number steps:' $LOGS/number-steps.log
    else
        fail "number keyboard integration"
        tail -25 $LOGS/number-steps.log
    fi

    step "popover transition integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . popover_beforetoggle_tests.tscn ) > $LOGS/popover-beforetoggle.log 2>&1 &&
       grep -q 'godot popover beforetoggle:.* 0 failures' $LOGS/popover-beforetoggle.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/popover-beforetoggle.log; then
        grep 'godot popover beforetoggle:' $LOGS/popover-beforetoggle.log
    else
        fail "popover transition integration"
        tail -25 $LOGS/popover-beforetoggle.log
    fi

    step "hidden-panel transitions"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . hidden_transition_tests.tscn ) > $LOGS/hidden-transitions.log 2>&1 &&
       grep -q 'Hidden transitions: 6 checks, 0 failures' $LOGS/hidden-transitions.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/hidden-transitions.log; then
        grep 'Hidden transitions:' $LOGS/hidden-transitions.log
    else
        fail "hidden-panel transitions"
        tail -25 $LOGS/hidden-transitions.log
    fi

    step "transition reversal"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . transition_reversal_tests.tscn ) > $LOGS/transition-reversal.log 2>&1 &&
       grep -q 'Transition reversal: 32 checks, 0 failures' $LOGS/transition-reversal.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/transition-reversal.log; then
        grep 'Transition reversal:' $LOGS/transition-reversal.log
    else
        fail "transition reversal"
        tail -25 $LOGS/transition-reversal.log
    fi

    step "transition cancellation"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . transition_cancellation_tests.tscn ) > $LOGS/transition-cancellation.log 2>&1 &&
       grep -q 'Transition cancellation: 18 checks, 0 failures' $LOGS/transition-cancellation.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/transition-cancellation.log; then
        grep 'Transition cancellation:' $LOGS/transition-cancellation.log
    else
        fail "transition cancellation"
        tail -25 $LOGS/transition-cancellation.log
    fi

    step "delayed transitions"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . delayed_transition_tests.tscn ) > $LOGS/delayed-transitions.log 2>&1 &&
       grep -q 'Delayed transitions: 6 checks, 0 failures' $LOGS/delayed-transitions.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/delayed-transitions.log; then
        grep 'Delayed transitions:' $LOGS/delayed-transitions.log
    else
        fail "delayed transitions"
        tail -25 $LOGS/delayed-transitions.log
    fi

    step "long animation and transition lists"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . long_effect_list_tests.tscn ) > $LOGS/long-effects.log 2>&1 &&
       grep -q 'Long effect lists: 16 checks, 0 failures' $LOGS/long-effects.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/long-effects.log; then
        grep 'Long effect lists:' $LOGS/long-effects.log
    else
        fail "long animation and transition lists"
        tail -25 $LOGS/long-effects.log
    fi

    step "incremental font warmup"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . font_warmup_tests.tscn ) > $LOGS/font-warmup.log 2>&1 &&
       grep -q 'Font warmup: 4 checks, 0 failures' $LOGS/font-warmup.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/font-warmup.log; then
        grep 'Font warmup:' $LOGS/font-warmup.log
    else
        fail "incremental font warmup"
        tail -25 $LOGS/font-warmup.log
    fi

    step "conditional keyframes and animation timing"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . conditional_keyframe_tests.tscn ) > $LOGS/keyframes.log 2>&1 &&
       grep -q 'Conditional keyframes: 204 checks, 0 failures' $LOGS/keyframes.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/keyframes.log; then
        grep 'Conditional keyframes:' $LOGS/keyframes.log
    else
        fail "conditional keyframes and animation timing"
        tail -25 $LOGS/keyframes.log
    fi

    step "CSS diagnostics"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . css_diagnostic_tests.tscn ) > $LOGS/css-diagnostics.log 2>&1 &&
       grep -q 'CSS diagnostics: 26 checks, 0 failures' $LOGS/css-diagnostics.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/css-diagnostics.log; then
        grep 'CSS diagnostics:' $LOGS/css-diagnostics.log
    else
        fail "CSS diagnostics"
        tail -25 $LOGS/css-diagnostics.log
    fi

    step "unknown at-rule containment"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . unknown_at_rule_tests.tscn ) > $LOGS/unknown-at-rules.log 2>&1 &&
       grep -q 'Unknown at-rule: 12 checks, 0 failures' $LOGS/unknown-at-rules.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/unknown-at-rules.log; then
        grep 'Unknown at-rule:' $LOGS/unknown-at-rules.log
    else
        fail "unknown at-rule containment"
        tail -25 $LOGS/unknown-at-rules.log
    fi

    step "select control integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . select_control_tests.tscn ) > $LOGS/select-controls.log 2>&1 &&
       grep -q 'godot select controls:.* 0 failures' $LOGS/select-controls.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/select-controls.log; then
        grep 'godot select controls:' $LOGS/select-controls.log
    else
        fail "select control integration"
        tail -25 $LOGS/select-controls.log
    fi

    step "select autoscroll integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . select_autoscroll_tests.tscn ) > $LOGS/select-autoscroll.log 2>&1 &&
       grep -q 'godot select autoscroll:.* 0 failures' $LOGS/select-autoscroll.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/select-autoscroll.log; then
        grep 'godot select autoscroll:' $LOGS/select-autoscroll.log
    else
        fail "select autoscroll integration"
        tail -25 $LOGS/select-autoscroll.log
    fi

    step "select typeahead integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . typeahead_tests.tscn ) > $LOGS/typeahead.log 2>&1 &&
       grep -q 'godot typeahead:.* 0 failures' $LOGS/typeahead.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' $LOGS/typeahead.log; then
        grep 'godot typeahead:' $LOGS/typeahead.log
    else
        fail "select typeahead integration"
        tail -25 $LOGS/typeahead.log
    fi

    # The demo, driven through its OWN markup and script. It is the
    # explanation of how to use this from GDScript, and nothing else in the
    # suite reads it -- so without this it rots silently while every other
    # gate stays green.
    scene_step "demo" demo_smoke.tscn "godot demo:"

    # A whole screen, built the way someone would actually build one: a
    # gamepad-navigable grid, live data, a detail pane following the
    # selection, an equip action and a filter. It exists to find gaps in the
    # BINDING SURFACE that adding one method at a time never surfaces -- it
    # found three on its first run.
    scene_step "inventory" inventory_demo.tscn "godot inventory:"

    # Two-way binding, from GDScript. The return path -- a `data-model`
    # control's value arriving back in the script's own dictionary -- is the
    # half no C++ test can reach, because the dictionary is Godot's.
    scene_step "bindings" binding_tests.tscn "godot bindings:"

    # Hover, through the input path a WINDOW uses rather than set_pointer.
    # The interactive gate drives the engine in document coordinates, which is
    # the one path that cannot get a transform wrong -- so it would stay green
    # while a scaled, panned document in a Control hovered nothing.
    scene_step "hover" hover_tests.tscn "godot hover:"

    # And in the gallery itself, which is the scene a person looks at samples
    # in: a Node2D inside a clipped Control inside two containers.
    scene_step "gallery hover" gallery_hover_test.tscn "godot gallery hover:"

    # A handler that reloads mid-delivery must not write through the stale
    # handle, and a normal element painted after a blended one must be on
    # top of it. The pixel half needs a renderer, like the gates above.
    step "layer order and reload delivery"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --rendering-driver opengl3 --path . layer_order_tests.tscn ) > $LOGS/layer-order.log 2>&1 &&
       grep 'godot layer order:' $LOGS/layer-order.log | tail -1 | grep -q '4 checks, 0 failures' &&
       ! grep -q 'SCRIPT ERROR:' $LOGS/layer-order.log; then
        grep 'godot layer order:' $LOGS/layer-order.log | tail -1
    else
        fail "layer order and reload delivery"
        tail -25 $LOGS/layer-order.log
    fi

    step "gallery animation clock"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . gallery_clock_tests.tscn ) > $LOGS/gallery-clock.log 2>&1 &&
       grep -q 'godot gallery clock: 1 checks, 0 failures' $LOGS/gallery-clock.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' $LOGS/gallery-clock.log; then
        grep 'godot gallery clock:' $LOGS/gallery-clock.log
    else
        fail "gallery animation clock"
        tail -25 $LOGS/gallery-clock.log
    fi
else
    skip "host tests (no godot at $GODOT)"
fi

printf '\n'
if [ "$release" -eq 1 ] && [ "$skipped" -gt 0 ]; then
    fail "release verification incomplete ($skipped skipped gate(s))"
fi
if [ "$failures" -eq 0 ]; then
    printf 'all gates pass'
    [ "$skipped" -gt 0 ] && printf ' (%d skipped)' "$skipped"
    printf '\n'
    exit 0
fi
printf '%d gate(s) failed\n' "$failures"
exit 1
