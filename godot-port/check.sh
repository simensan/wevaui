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

ROOT="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$ROOT/.." && pwd)"
GCC=${WEVA_BUILD_GCC:-$HOME/weva/build-gcc}
CLANG=${WEVA_BUILD_CLANG:-$HOME/weva/build-clang}
GODOT_BUILD=${WEVA_BUILD_GODOT:-$HOME/weva/build-godot}
GODOT=${GODOT_BIN:-$HOME/godot/godot}
SAMPLES="$ROOT/tools/oracle/corpus/samples"

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
step "performance verification tools"
python3 -m unittest discover -s "$ROOT/hosts/godot" -p 'test_frontier_perf*.py' \
    || fail "performance verification tools"

step "build"
if [ ! -f "$GCC/build.ninja" ]; then
    if ! cmake -S "$ROOT" -B "$GCC" -G Ninja -DCMAKE_BUILD_TYPE=Release \
            > /tmp/weva-configure.log 2>&1; then
        cat /tmp/weva-configure.log
        exit 1
    fi
fi
if ! ( cd "$GCC" && ninja ) > /tmp/weva-build.log 2>&1; then
    fail "release build"
    grep -E "error:|FAILED" /tmp/weva-build.log | head -5
    exit 1 # A stale binary must not supply evidence after a failed build.
else
    echo "release ok"
fi

# ---- complete core suite -------------------------------------------------
# CTest owns the suite list, including the benchmark CLI and allocation guards.
# Check process exits, not a trailing success string from a crashed process.
step "core tests and incremental corpus"
if WEVA_INCREMENTAL_CORPUS="$SAMPLES" ctest --test-dir "$GCC" \
        --output-on-failure --no-tests=error > /tmp/weva-core.log 2>&1; then
    cat /tmp/weva-core.log
else
    fail "core tests and incremental corpus"
    tail -45 /tmp/weva-core.log
fi

# ---- sanitizers ----------------------------------------------------------
step "sanitizers"
if [ -f "$CLANG/CMakeCache.txt" ]; then
    # Configure the actual instrumentation and positive controls. Merely
    # naming a directory "build-clang" does not prove sanitizers are active.
    if ! cmake -S "$ROOT" -B "$CLANG" -DWEVA_SANITIZERS=ON -DWEVA_BUILD_TESTS=ON \
            > /tmp/weva-san-build.log 2>&1 ||
       ! cmake --build "$CLANG" >> /tmp/weva-san-build.log 2>&1; then
        fail "sanitizer configure/build"
        tail -20 /tmp/weva-san-build.log
    else
        # Include retained/full sample mutations, allocation guards and CLI
        # checks, and require the expected ASan/UBSan positive-control reports.
        if WEVA_INCREMENTAL_CORPUS="$SAMPLES" ctest --test-dir "$CLANG" \
                --output-on-failure --no-tests=error > /tmp/weva-sanitizers.log 2>&1; then
            cat /tmp/weva-sanitizers.log
        else
            fail "sanitizers"
            tail -45 /tmp/weva-sanitizers.log
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
step "layout oracle"
if command -v python3 > /dev/null && [ -x "$GCC/tools/weva_dump/weva_dump" ]; then
    oracle() {
        local corpus="$1" width="$2" height="$3" allowed="$4" status=0
        local log="/tmp/weva-oracle-$(basename "$corpus").log"
        (cd "$REPO" && python3 "$ROOT/tools/oracle/run_oracle.py" "$corpus" \
            --width "$width" --height "$height" --weva-dump "$GCC/tools/weva_dump/weva_dump" \
            --out-dir "/tmp/weva-oracle-$(basename "$corpus")" --quiet) > "$log" 2>&1 || status=$?
        if ! python3 "$ROOT/tools/check_oracle_summary.py" --log "$log" \
                --corpus "$corpus" --exit-code "$status" --max-differences "$allowed"; then
            fail "layout oracle ($(basename "$corpus")); see $log"
        fi
    }
    oracle "$SAMPLES" 1280 720 0
    oracle "$ROOT/tools/oracle/corpus/hand" 800 600 0
    # Keep the existing development allowance; a release requires zero findings.
    allowed_harvest=3
    [ "$release" -eq 0 ] || allowed_harvest=0
    oracle "$ROOT/tools/oracle/corpus/harvest" 800 600 "$allowed_harvest"
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
    if python3 "$ROOT/tools/check_cached_ids.py" > /tmp/weva-cached-ids.log 2>&1; then
        tail -1 /tmp/weva-cached-ids.log
    else
        fail "cached ids"
        cat /tmp/weva-cached-ids.log
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
        exit 1 # Do not package or verify the previously installed extension.
    fi
else
    skip "godot extension (needs godot and a configured $GODOT_BUILD)"
fi

# ---- engine text safety, independent of the addon and its test font ------
# Ordinary interaction fixtures avoid the known long-emoji engine crash.
# Their success therefore cannot establish safety for unrestricted input.
step "Godot engine text safety"
if [ -x "$GODOT" ] && command -v python3 > /dev/null; then
    shaping_temporary=$(mktemp -d) || exit 1
    if python3 "$ROOT/tools/godot-text-shaping-repro/check.py" --godot "$GODOT" \
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
    if python3 "$ROOT/tools/godot-text-shaping-repro/check_exports.py" --godot "$GODOT" \
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
            --library "$font_test_library" > /tmp/weva-font-shaping.log 2>&1; then
        cat /tmp/weva-font-shaping.log
    else
        fail "Godot font adapter"
        tail -25 /tmp/weva-font-shaping.log
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
            --output "$addon_temporary/weva-addon.zip" > /tmp/weva-export.log 2>&1 && \
       python3 "$ROOT/hosts/godot/check_export.py" --godot "$GODOT" \
            --addon "$addon_temporary/weva-addon.zip" "${export_checks[@]}" "${template_args[@]}" >> /tmp/weva-export.log 2>&1; then
        cat /tmp/weva-export.log
    else
        fail "Godot addon installation and native exports"
        tail -25 /tmp/weva-export.log
    fi
    printf 'Addon artifacts: %s\n' "$addon_temporary"
else
    skip "Godot addon installation and native exports (needs Godot editor, python3 and configured host)"
fi

# ---- the two rasterisers, on the same draw list --------------------------
step "backend gate"
if [ -x "$GODOT" ] && [ -x "$GCC/tools/weva_render/weva_render" ]; then
    if ! WEVA_RENDER="$GCC/tools/weva_render/weva_render" GODOT_BIN="$GODOT" \
        bash "$ROOT/hosts/godot/compare_all.sh" "$SAMPLES" > /tmp/weva-render.txt 2>&1; then
        fail "backend comparison process"
        tail -15 /tmp/weva-render.txt
    fi
    worst=$(grep -c 'struct   [1-9]' /tmp/weva-render.txt || true)
    echo "$(wc -l < /tmp/weva-render.txt) samples, $worst over the structural gate"
    [ "$worst" = "0" ] || { fail "backend gate"; sort -t% -k1 -rn /tmp/weva-render.txt | head -3; }

    step "interactive gate"
    if ! WEVA_RENDER="$GCC/tools/weva_render/weva_render" GODOT_BIN="$GODOT" \
        bash "$ROOT/hosts/godot/compare_live.sh" > /tmp/weva-live.txt 2>&1; then
        fail "interactive comparison process"
    fi
    cat /tmp/weva-live.txt
    grep -q 'ERR' /tmp/weva-live.txt && fail "interactive gate"
    grep -qE 'struct +[1-9]' /tmp/weva-live.txt && fail "interactive gate"
    # The colour figure too, not only the structural one. A missing FILL is a
    # colour difference and barely moves the structural number: the hover bug
    # this gate was added to catch read 0.11% structural and 4.18% colour, so
    # the structural rule alone would have passed it. Every state is 0.00% on
    # both now, so anything reaching one percent is a real change.
    grep -qE 'over-tol +[1-9]' /tmp/weva-live.txt && fail "interactive gate"
    step "triangle batching pixels"
    if python3 "$ROOT/hosts/godot/check_triangle_batching.py" --godot "$GODOT" \
            --project "$ROOT/hosts/godot/project" > /tmp/weva-triangle-batching.log 2>&1; then
        grep 'triangle batching: 6 identical images' /tmp/weva-triangle-batching.log
    else
        fail "triangle batching pixels"
        tail -25 /tmp/weva-triangle-batching.log
    fi
    step "packed draw cache pixels"
    if python3 "$ROOT/hosts/godot/check_packed_draws.py" --godot "$GODOT" \
            --project "$ROOT/hosts/godot/project" > /tmp/weva-packed-draws.log 2>&1; then
        grep 'identical images' /tmp/weva-packed-draws.log
    else
        fail "packed draw cache pixels"
        tail -25 /tmp/weva-packed-draws.log
    fi
    step "glyph preparation cache pixels"
    if python3 "$ROOT/hosts/godot/check_packed_draws.py" --godot "$GODOT" \
            --project "$ROOT/hosts/godot/project" --cache-kind glyphs > /tmp/weva-glyph-reuse.log 2>&1; then
        grep 'identical images' /tmp/weva-glyph-reuse.log
    else
        fail "glyph preparation cache pixels"
        tail -25 /tmp/weva-glyph-reuse.log
    fi
else
    skip "backend gates (needs godot and weva_render)"
fi

# ---- the GDScript surface ------------------------------------------------
step "host tests"
if [ -x "$GODOT" ]; then
    # Built above, before the backend gate that depends on it.
    if (cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
        "$GODOT" --headless --path . test_scene.tscn) > /tmp/weva-host.log 2>&1 &&
       grep -q 'godot host:.* checks, 0 failures' /tmp/weva-host.log &&
       ! grep -qE 'ERROR:|^FAIL' /tmp/weva-host.log; then
        grep 'godot host:' /tmp/weva-host.log
    else
        fail "host tests"
        tail -25 /tmp/weva-host.log
    fi

    # Native routing is distinct from the direct ABI/input methods exercised
    # by the host suite. Keep focus, stacking and Control sizing in the gate.
    step "native input integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . input_integration_tests.tscn ) > /tmp/weva-input.log 2>&1 &&
       grep -q 'godot input integration:.* 0 failures' /tmp/weva-input.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-input.log; then
        grep 'godot input integration:' /tmp/weva-input.log
    else
        fail "native input integration"
        tail -25 /tmp/weva-input.log
    fi

    step "keyboard integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . keyboard_integration_tests.tscn ) > /tmp/weva-keyboard.log 2>&1 &&
       grep -q 'godot keyboard integration:.* 0 failures' /tmp/weva-keyboard.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-keyboard.log; then
        grep 'godot keyboard integration:' /tmp/weva-keyboard.log
    else
        fail "keyboard integration"
        tail -25 /tmp/weva-keyboard.log
    fi

    step "IME integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . ime_integration_tests.tscn ) > /tmp/weva-ime.log 2>&1 &&
       grep -q 'godot IME integration:.* 0 failures' /tmp/weva-ime.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-ime.log; then
        grep 'godot IME integration:' /tmp/weva-ime.log
    else
        fail "IME integration"
        tail -25 /tmp/weva-ime.log
    fi

    step "theme font integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . theme_font_tests.tscn ) > /tmp/weva-theme-font.log 2>&1 &&
       grep -q 'godot theme fonts:.* 0 failures' /tmp/weva-theme-font.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-theme-font.log; then
        grep 'godot theme fonts:' /tmp/weva-theme-font.log
    else
        fail "theme font integration"
        tail -25 /tmp/weva-theme-font.log
    fi

    step "font face integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . font_face_tests.tscn ) > /tmp/weva-font-face.log 2>&1 &&
       grep -q 'godot font face:.* 0 failures' /tmp/weva-font-face.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-font-face.log; then
        grep 'godot font face:' /tmp/weva-font-face.log
    else
        fail "font face integration"
        tail -25 /tmp/weva-font-face.log
    fi

    step "line height integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . line_height_tests.tscn ) > /tmp/weva-line-height.log 2>&1 &&
       grep -q 'godot line height:.* 0 failures' /tmp/weva-line-height.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-line-height.log; then
        grep 'godot line height:' /tmp/weva-line-height.log
    else
        fail "line height integration"
        tail -25 /tmp/weva-line-height.log
    fi

    step "font inheritance integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . font_inheritance_tests.tscn ) > /tmp/weva-font-inheritance.log 2>&1 &&
       grep -q 'godot font inheritance:.* 0 failures' /tmp/weva-font-inheritance.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-font-inheritance.log; then
        grep 'godot font inheritance:' /tmp/weva-font-inheritance.log
    else
        fail "font inheritance integration"
        tail -25 /tmp/weva-font-inheritance.log
    fi

    step "font-size context integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . font_size_tests.tscn ) > /tmp/weva-font-size.log 2>&1 &&
       grep -q 'godot font-size context:.* 0 failures' /tmp/weva-font-size.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-font-size.log; then
        grep 'godot font-size context:' /tmp/weva-font-size.log
    else
        fail "font-size context integration"
        tail -25 /tmp/weva-font-size.log
    fi

    step "intrinsic sizing integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . intrinsic_size_tests.tscn ) > /tmp/weva-intrinsic-size.log 2>&1 &&
       grep -q 'godot intrinsic sizing: 336 checks, 0 failures' /tmp/weva-intrinsic-size.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-intrinsic-size.log; then
        grep 'godot intrinsic sizing:' /tmp/weva-intrinsic-size.log
    else
        fail "intrinsic sizing integration"
        tail -25 /tmp/weva-intrinsic-size.log
    fi

    step "text editing integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . text_editing_tests.tscn ) > /tmp/weva-text-editing.log 2>&1 &&
       grep -q 'godot text editing:.* 0 failures' /tmp/weva-text-editing.log &&
       ! grep -qE 'ERROR:|Unicode parsing error|FAIL ' /tmp/weva-text-editing.log; then
        grep 'godot text editing:' /tmp/weva-text-editing.log
    else
        fail "text editing integration"
        tail -25 /tmp/weva-text-editing.log
    fi

    step "text selection autoscroll integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . text_autoscroll_tests.tscn ) > /tmp/weva-text-autoscroll.log 2>&1 &&
       grep -q 'godot text autoscroll:.* 0 failures' /tmp/weva-text-autoscroll.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-text-autoscroll.log; then
        grep 'godot text autoscroll:' /tmp/weva-text-autoscroll.log
    else
        fail "text selection autoscroll integration"
        tail -25 /tmp/weva-text-autoscroll.log
    fi

    step "maxlength and paste integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . maxlength_tests.tscn ) > /tmp/weva-maxlength.log 2>&1 &&
       grep -q 'godot maxlength:.* 0 failures' /tmp/weva-maxlength.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-maxlength.log; then
        grep 'godot maxlength:' /tmp/weva-maxlength.log
    else
        fail "maxlength and paste integration"
        tail -25 /tmp/weva-maxlength.log
    fi

    step "form state and reset integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . form_state_tests.tscn ) > /tmp/weva-form-state.log 2>&1 &&
       grep -q 'godot form state:.* 0 failures' /tmp/weva-form-state.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-form-state.log; then
        grep 'godot form state:' /tmp/weva-form-state.log
    else
        fail "form state and reset integration"
        tail -25 /tmp/weva-form-state.log
    fi

    step "dialog cancellation and result integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . dialog_cancel_tests.tscn ) > /tmp/weva-dialog-cancel.log 2>&1 &&
       grep -q 'godot dialog cancellation:.* 0 failures' /tmp/weva-dialog-cancel.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-dialog-cancel.log; then
        grep 'godot dialog cancellation:' /tmp/weva-dialog-cancel.log
    else
        fail "dialog cancellation and result integration"
        tail -25 /tmp/weva-dialog-cancel.log
    fi

    step "form validation integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . form_validation_tests.tscn ) > /tmp/weva-form-validation.log 2>&1 &&
       grep -q 'godot form validation:.* 0 failures' /tmp/weva-form-validation.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-form-validation.log; then
        grep 'godot form validation:' /tmp/weva-form-validation.log
    else
        fail "form validation integration"
        tail -25 /tmp/weva-form-validation.log
    fi

    step "validity selector integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . validity_selector_tests.tscn ) > /tmp/weva-validity-selectors.log 2>&1 &&
       grep -q 'godot validity selectors:.* 0 failures' /tmp/weva-validity-selectors.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-validity-selectors.log; then
        grep 'godot validity selectors:' /tmp/weva-validity-selectors.log
    else
        fail "validity selector integration"
        tail -25 /tmp/weva-validity-selectors.log
    fi

    step "multicol sizing integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . multicol_sizing_tests.tscn ) > /tmp/weva-multicol-sizing.log 2>&1 &&
       grep -q 'godot multicol sizing:.* 0 failures' /tmp/weva-multicol-sizing.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-multicol-sizing.log; then
        grep 'godot multicol sizing:' /tmp/weva-multicol-sizing.log
    else
        fail "multicol sizing integration"
        tail -25 /tmp/weva-multicol-sizing.log
    fi

    step "block margin integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . block_margin_tests.tscn ) > /tmp/weva-block-margins.log 2>&1 &&
       grep -q 'godot block margins:.* 0 failures' /tmp/weva-block-margins.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-block-margins.log; then
        grep 'godot block margins:' /tmp/weva-block-margins.log
    else
        fail "block margin integration"
        tail -25 /tmp/weva-block-margins.log
    fi

    step "cumulative timing integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . timing_tests.tscn ) > /tmp/weva-timing.log 2>&1 &&
       grep -q 'godot timing:.* 0 failures' /tmp/weva-timing.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-timing.log; then
        grep 'godot timing:' /tmp/weva-timing.log
    else
        fail "cumulative timing integration"
        tail -25 /tmp/weva-timing.log
    fi

    step "input geometry integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . input_geometry_tests.tscn ) > /tmp/weva-input-geometry.log 2>&1 &&
       grep -q 'godot input geometry:.* 0 failures' /tmp/weva-input-geometry.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-input-geometry.log; then
        grep 'godot input geometry:' /tmp/weva-input-geometry.log
    else
        fail "input geometry integration"
        tail -25 /tmp/weva-input-geometry.log
    fi

    step "range direction integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . range_direction_tests.tscn ) > /tmp/weva-range-direction.log 2>&1 &&
       grep -q 'range direction:.* 0 failures' /tmp/weva-range-direction.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-range-direction.log; then
        grep 'range direction:' /tmp/weva-range-direction.log
    else
        fail "range direction integration"
        tail -25 /tmp/weva-range-direction.log
    fi

    step "number keyboard integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . number_step_tests.tscn ) > /tmp/weva-number-steps.log 2>&1 &&
       grep -q 'number steps:.* 0 failures' /tmp/weva-number-steps.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-number-steps.log; then
        grep 'number steps:' /tmp/weva-number-steps.log
    else
        fail "number keyboard integration"
        tail -25 /tmp/weva-number-steps.log
    fi

    step "popover transition integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . popover_beforetoggle_tests.tscn ) > /tmp/weva-popover-beforetoggle.log 2>&1 &&
       grep -q 'godot popover beforetoggle:.* 0 failures' /tmp/weva-popover-beforetoggle.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-popover-beforetoggle.log; then
        grep 'godot popover beforetoggle:' /tmp/weva-popover-beforetoggle.log
    else
        fail "popover transition integration"
        tail -25 /tmp/weva-popover-beforetoggle.log
    fi

    step "hidden-panel transitions"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . hidden_transition_tests.tscn ) > /tmp/weva-hidden-transitions.log 2>&1 &&
       grep -q 'Hidden transitions: 6 checks, 0 failures' /tmp/weva-hidden-transitions.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-hidden-transitions.log; then
        grep 'Hidden transitions:' /tmp/weva-hidden-transitions.log
    else
        fail "hidden-panel transitions"
        tail -25 /tmp/weva-hidden-transitions.log
    fi

    step "transition reversal"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . transition_reversal_tests.tscn ) > /tmp/weva-transition-reversal.log 2>&1 &&
       grep -q 'Transition reversal: 32 checks, 0 failures' /tmp/weva-transition-reversal.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-transition-reversal.log; then
        grep 'Transition reversal:' /tmp/weva-transition-reversal.log
    else
        fail "transition reversal"
        tail -25 /tmp/weva-transition-reversal.log
    fi

    step "transition cancellation"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . transition_cancellation_tests.tscn ) > /tmp/weva-transition-cancellation.log 2>&1 &&
       grep -q 'Transition cancellation: 18 checks, 0 failures' /tmp/weva-transition-cancellation.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-transition-cancellation.log; then
        grep 'Transition cancellation:' /tmp/weva-transition-cancellation.log
    else
        fail "transition cancellation"
        tail -25 /tmp/weva-transition-cancellation.log
    fi

    step "delayed transitions"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . delayed_transition_tests.tscn ) > /tmp/weva-delayed-transitions.log 2>&1 &&
       grep -q 'Delayed transitions: 6 checks, 0 failures' /tmp/weva-delayed-transitions.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-delayed-transitions.log; then
        grep 'Delayed transitions:' /tmp/weva-delayed-transitions.log
    else
        fail "delayed transitions"
        tail -25 /tmp/weva-delayed-transitions.log
    fi

    step "long animation and transition lists"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . long_effect_list_tests.tscn ) > /tmp/weva-long-effects.log 2>&1 &&
       grep -q 'Long effect lists: 16 checks, 0 failures' /tmp/weva-long-effects.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-long-effects.log; then
        grep 'Long effect lists:' /tmp/weva-long-effects.log
    else
        fail "long animation and transition lists"
        tail -25 /tmp/weva-long-effects.log
    fi

    step "incremental font warmup"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . font_warmup_tests.tscn ) > /tmp/weva-font-warmup.log 2>&1 &&
       grep -q 'Font warmup: 4 checks, 0 failures' /tmp/weva-font-warmup.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-font-warmup.log; then
        grep 'Font warmup:' /tmp/weva-font-warmup.log
    else
        fail "incremental font warmup"
        tail -25 /tmp/weva-font-warmup.log
    fi

    step "conditional keyframes and animation timing"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . conditional_keyframe_tests.tscn ) > /tmp/weva-keyframes.log 2>&1 &&
       grep -q 'Conditional keyframes: 204 checks, 0 failures' /tmp/weva-keyframes.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-keyframes.log; then
        grep 'Conditional keyframes:' /tmp/weva-keyframes.log
    else
        fail "conditional keyframes and animation timing"
        tail -25 /tmp/weva-keyframes.log
    fi

    step "CSS diagnostics"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . css_diagnostic_tests.tscn ) > /tmp/weva-css-diagnostics.log 2>&1 &&
       grep -q 'CSS diagnostics: 26 checks, 0 failures' /tmp/weva-css-diagnostics.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-css-diagnostics.log; then
        grep 'CSS diagnostics:' /tmp/weva-css-diagnostics.log
    else
        fail "CSS diagnostics"
        tail -25 /tmp/weva-css-diagnostics.log
    fi

    step "unknown at-rule containment"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . unknown_at_rule_tests.tscn ) > /tmp/weva-unknown-at-rules.log 2>&1 &&
       grep -q 'Unknown at-rule: 12 checks, 0 failures' /tmp/weva-unknown-at-rules.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-unknown-at-rules.log; then
        grep 'Unknown at-rule:' /tmp/weva-unknown-at-rules.log
    else
        fail "unknown at-rule containment"
        tail -25 /tmp/weva-unknown-at-rules.log
    fi

    step "select control integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . select_control_tests.tscn ) > /tmp/weva-select-controls.log 2>&1 &&
       grep -q 'godot select controls:.* 0 failures' /tmp/weva-select-controls.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-select-controls.log; then
        grep 'godot select controls:' /tmp/weva-select-controls.log
    else
        fail "select control integration"
        tail -25 /tmp/weva-select-controls.log
    fi

    step "select autoscroll integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . select_autoscroll_tests.tscn ) > /tmp/weva-select-autoscroll.log 2>&1 &&
       grep -q 'godot select autoscroll:.* 0 failures' /tmp/weva-select-autoscroll.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-select-autoscroll.log; then
        grep 'godot select autoscroll:' /tmp/weva-select-autoscroll.log
    else
        fail "select autoscroll integration"
        tail -25 /tmp/weva-select-autoscroll.log
    fi

    step "select typeahead integration"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . typeahead_tests.tscn ) > /tmp/weva-typeahead.log 2>&1 &&
       grep -q 'godot typeahead:.* 0 failures' /tmp/weva-typeahead.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL  ' /tmp/weva-typeahead.log; then
        grep 'godot typeahead:' /tmp/weva-typeahead.log
    else
        fail "select typeahead integration"
        tail -25 /tmp/weva-typeahead.log
    fi

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

    step "gallery animation clock"
    if ( cd "$ROOT/hosts/godot/project" && GODOT_SILENCE_ROOT_WARNING=1 timeout 300 \
            "$GODOT" --headless --path . gallery_clock_tests.tscn ) > /tmp/weva-gallery-clock.log 2>&1 &&
       grep -q 'godot gallery clock: 1 checks, 0 failures' /tmp/weva-gallery-clock.log &&
       ! grep -qE 'SCRIPT ERROR:|FAIL ' /tmp/weva-gallery-clock.log; then
        grep 'godot gallery clock:' /tmp/weva-gallery-clock.log
    else
        fail "gallery animation clock"
        tail -25 /tmp/weva-gallery-clock.log
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
