# Godot script-iterator stack reproduction

This standalone TextServer project has no Weva dependency or extension.
It reproduces malformed glyph arrays and process crashes in official Godot
4.7.2 on Windows and Linux, commit
`ed1daf0bf001b61586d9930840f2f1394092c079`.

## Run

From the repository root, run:

```sh
python3 Tools/godot-text-shaping-repro/check.py \
  --godot /path/to/godot --logs /path/to/shaping-results
```

The runner copies the two-file project to the new artifact directory and
launches each case in a separate process with a 30-second timeout. No import
or source-project cache is required. Existing artifact directories are rejected
so results cannot be overwritten or mixed with an earlier engine run.
There is no rendering, native input, or dynamic extension. The theme font and
installed symbol/emoji fonts are used. Six cases check 32, 33, 65 and 256
alternating Latin/emoji runs, then multiple scripts after stack growth. The
short-unit references establish the expected source ranges and advances;
these fixture strings have no cross-unit kerning or joining.

Each case retains its unfiltered process output and a separate engine log.
`result.json` records the engine/probe/explicit ICU-data SHA256 values, exact
commands, exit codes, elapsed times and failure reasons. A zero exit code,
the correct case's complete assertion summary, and no engine/assertion errors
are all required. Crashes, timeouts, launch errors and missing summaries fail.
Even an unrelated environment error remains a failure; inspect the raw logs
to distinguish it from the shaping defect.

`check.sh` now runs this suite as **Godot engine text safety**.
Use `bash check.sh --release` to also reject skipped gates. This
checks the configured editor; runtime templates still need their own run
with ICU data as shown below. The release mode is stricter automation, not
evidence that unimplemented product requirements are complete.

The runner's failure handling can be checked without a Godot process:

```sh
python3 -m unittest discover -s Tools/godot-text-shaping-repro -p test_check.py
```

To run one case directly:

```sh
godot --headless --path Tools/godot-text-shaping-repro \
  --script res://probe.gd -- multiple_scripts
```

## Cause and patch

### Exported games

`check_exports.py` tests the editor's actual debug and release exports, including
templates compiled with path overrides disabled:

```sh
python3 Tools/godot-text-shaping-repro/check_exports.py \
  --godot /path/to/editor --debug-template /path/to/debug-template \
  --release-template /path/to/release-template --logs /new/export-results
```

Both custom template arguments are optional together; omitting them tests the
installed templates. The fixture embeds ICU support data, checks that it loaded,
hides the source project and relocates the games before launch. Every build mode
must report its correct debug/release identity and pass every case. The runtime
probe preserves the standalone assertions while adapting the entry point to a
scene. Logs, process exits, executable/pack hashes and selected template hashes
are retained. `check.sh` runs this gate independently of editor checks.

### Engine change

In `modules/text_server_adv/script_iterator.cpp`, the first heap allocations
for the emoji and parentheses stacks omit their existing stack entries. The
emoji buffer is freed at the end of each script run without resetting the
pointer/capacity, causing use-after-free and double-free when another script
follows. The threshold for the emoji stack is 32 separate runs, not 32
consecutive emoji or 32 characters.

[`godot-script-iterator.patch`](godot-script-iterator.patch) copies existing
entries on first growth, moves the emoji free outside the script loop and
cleans it up on the ICU error path. Apply to the source commit above:

```sh
git apply /path/to/unityui/Tools/godot-text-shaping-repro/godot-script-iterator.patch
scons -j10 platform=linuxbsd target=template_debug optimize=speed \
  debug_symbols=no use_lto=no vulkan=no opengl3=yes wayland=no \
  disable_3d=yes disable_path_overrides=no module_mono_enabled=no accesskit=no
```

**Runtime templates need ICU support data for this reproduction.** The editor
embeds it, but these template builds do not. Without it, Godot bypasses the
script detection involved here, producing a false pass. The probe checks
Turkish case conversion before testing to catch that configuration mistake.

```sh
python3 Tools/godot-text-shaping-repro/check.py \
  --godot /path/to/godot-source/bin/godot.linuxbsd.template_debug.x86_64 \
  --support-data /path/to/godot-source/thirdparty/icu4c/icudt_godot.dat \
  --logs /path/to/patched-results
```

These options build a private diagnostic executable. The patch has not been
submitted upstream and does not modify or ship with the addon libraries.
Godot's license accompanies the patch in [GODOT_LICENSE.txt](GODOT_LICENSE.txt).

## Verification, 2026-09-06

| Engine | 32 runs | Remaining five cases |
|---|---|---|
| Official Windows 4.7.2 | Pass | All fail, including process crashes |
| Official Linux 4.7.2 | Pass | All fail, including double-free aborts |
| Matched unpatched Linux template + ICU data | Pass | All fail |
| Matched patched Linux template + ICU data | Pass | All pass |

The matched builds share source and settings; only the included script-iterator
patch differs. Both retain an unrelated local X11 IME patch, which is inactive
in this headless TextServer test. The patched build passes 18 checks and also
the Weva 81-check native autoscroll suite with its original 60-run input:

```sh
GODOT_TEXT_SUPPORT_DATA=/path/to/godot-source/thirdparty/icu4c/icudt_godot.dat \
WEVA_TEXT_SHAPING_STRESS=1 /path/to/patched-godot --headless \
  --path hosts/godot/project text_autoscroll_tests.tscn
```

This is focused regression evidence, not a complete validation of Godot's text
engine. The deep-parenthesis copy fix follows the same allocation invariant;
these six public-API cases specifically exercise the emoji-stack failure.
