# Chrome oracle

Chrome is the reference for CSS and HTML behavior. The tracked `hand`, `harvest`
and `samples` corpora are checked by `chrome_sweep.py`; `check.sh` runs all three
with browser metrics and a 1.5px maximum disagreement. Missing captures,
unmatched visible elements, crashes and empty selections fail. Exceptions must
be named with a reason in `known-gaps/chrome-sweep.txt`.

From the repository root, after building the core:

```sh
python3 Tools/oracle/chrome_sweep.py Tools/oracle/corpus/samples \
    --weva-dump <build>/Tools/weva_dump/weva_dump --width 1280 --height 720 \
    --chrome-metrics --max-worst 1.5 \
    --known-gaps Tools/oracle/known-gaps/chrome-sweep.txt
```

The `hand` and `harvest` corpora use 800×600. In WSL/Linux, `run.sh` checks all
three, or one named corpus. It uses the same `WEVA_BUILD_GCC` convention as
`check.sh`; `DUMP` can name a different executable and `WEVA_ORACLE_OUT` selects
the evidence directory.

```sh
bash Tools/oracle/run.sh
DUMP=/path/to/weva_dump bash Tools/oracle/run.sh samples
```

`run_oracle.py` is a compatibility entry point to the same Chrome gate. It adds
`--chrome-metrics --max-worst 1.5` by default. The C# engine and BaselineGen were
deleted; `--reuse-reference`, C# baseline generation and three-way arbitration
are retired. The shared exact comparison helper remains for host translation
checks. [Current readiness](../../docs/PRODUCT_READINESS.md) records the counts;
[ORACLE.md](../../docs/ORACLE.md) preserves the historical design and findings.

## Capturing and adding cases

Install the root Node dependencies and select Chrome with `WEVA_CHROME` (CI uses
a pinned executable). The capture helper requires an explicit directory and
supports two separate modes:

```sh
# Synthetic metrics for automated layout comparison.
node Tools/Layout/capture-all-chrome-layouts.mjs Tools/oracle/corpus/hand \
    800 600 --metrics=mono

# Real fonts for visual review; preserve the synthetic layout JSON.
node Tools/Layout/capture-all-chrome-layouts.mjs Tools/oracle/corpus/samples \
    1280 720 --metrics=inter --screenshot --no-layout
```

Synthetic captures use the bundled metric fonts and an engine UA stylesheet
overlay. They normalize computed `line-height: normal`, freeze motion and
prepare the declarative component fixtures. This checks the authored subset;
the focused live-browser scripts in `run_chrome_checks.py` check native browser
behavior without treating this broad corpus as a complete web conformance suite.

The sample collector reads `Assets/UI`, package samples and `Tools/oracle/cases`.
Use a staging output, inspect its changes, then bring the intended cases into the
tracked corpus. Some tracked cases are maintained directly in the corpus.

```sh
python3 Tools/oracle/collect_samples.py . --out .utmp/collected-samples
```

Local bitmap references are rewritten to per-page asset directories. Missing
images fail collection; `check.sh` also renders every sample to check that its
assets load. The historic harvest scripts extract cases from archived C# tests;
the current gate uses the tracked corpus and does not need those deleted tests.

If new text requires synthetic-font coverage, regenerate with `make_mono_font.py`
and recapture affected pages. Inspect a real render for every visual change.

## Host and image checks

`hosts/unity/oracle_from_unity.py` compares Unity and standalone core dumps with
identical synthetic metrics and frozen motion. This checks host translation.
`hosts/unity/goldens_from_unity.py` produces fresh Unity/Chrome PNG pairs for a
person to inspect, preserving tracked captures. Its report does not grade pixels.

`hosts/godot/compare_render.py` compares rasterizers consuming the same core draw
list. `chrome_survey.py` is an investigation tool: flat-cell color matches can
hide geometry and text differences, so its numbers are not a conformance score.
Font selection, sampling, antialiasing and animation state matter when comparing
screenshots; review the actual pictures.

The benchmark and renderer shell wrappers use `WEVA_BUILD_GCC` to locate the
current build, with `WEVA_BENCH` / `WEVA_RENDER` executable overrides.
`layoutbench.sh` and `flipbench.sh` fail on incomplete measurements and report
where failed-run logs were preserved. `survey_all.sh` uses the corpus supplied
on its command line, also exposed to the image tools as `WEVA_CORPUS`.

Chrome PNG pixels are sRGB bytes. For numerical software-renderer comparisons,
use `SoftwareRenderer::to_srgb_rgba()` rather than multiplying linear `pixel()`
channels by 255. The focused opacity checks cover this conversion.
