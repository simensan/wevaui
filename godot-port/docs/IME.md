# IME composition

The Godot host activates the platform input method when an editable HTML text
field has both GUI and window focus. Preedit appears in the field, with an
underline and the input method's selection. Its provisional value reaches
`data-model` and bound labels, as HTML input does in Chrome. Committing replaces
the preedit once, even when Godot delivers the result as several Unicode key
events. The whole composition is one undo step.

Cancellation removes the preedit, including any selected text it replaced.
Focus loss keeps the current preedit. Readonly and disabled controls reject
composition. Reloading or removing a focused field drops its composition
state. Script or binding changes cannot leave a stale replacement range in
the core. A cancelled insertion that restores the original value adds no
extra undo checkpoint.

## GDScript surface

Ordinary desktop input needs no manual forwarding. These signals expose the
composition lifecycle; `text_entered` reports the committed result once:

```gdscript
ui.composition_started.connect(func(id, text): print(id, " started: ", text))
ui.composition_updated.connect(func(id, text): print(id, " preedit: ", text))
ui.composition_ended.connect(func(id, text): print(id, " ended: ", text))
```

`has_composition()` distinguishes provisional input from a finished value.
These are queued host signals, not synchronous browser DOM events. In
particular, the final `value_changed` callback may observe composition already
ended. Use the composition signals to track the editing lifecycle.

Hosts providing their own text input can call:

```gdscript
ui.set_composition("日本", 0, 2) # Selection within preedit, in Godot characters.
ui.commit_composition("日本語")
ui.commit_composition("")       # Cancel an active preedit.
ui.finish_composition()         # Keep the current preedit and selection.
```

`get_caret_bounds()` returns the insertion rectangle in document coordinates.
`get_caret_window_bounds()` also includes canvas, Control and viewport
transforms. The host uses its lower edge for the OS candidate anchor and
refreshes IME focus with changed caret rendering, including X11 focus handoffs.

C ABI minor version 5 appends composition events and APIs without changing
the `weva_event` layout. Core selection offsets are UTF-8 bytes; the Godot
composition methods convert character offsets at the boundary. The legacy
eight-byte event text field holds a valid UTF-8 prefix. Call
`weva_document_event_text` after polling for the complete queued text, before
polling another event. This also fixes truncation of long pasted text and
committed Unicode in Godot signals.

## Evidence and limits (2026-09-06)

- Chrome 151 passes 40 composition oracle checks for input and textarea:
  preedit, selection replacement, commit, cancel, blur, disable, undo/redo,
  cancelled insertion and long Unicode results.
- Windows and Linux Godot 4.7.2 pass 28 integration checks for composition
  APIs, GUI result-key batching, bindings, focus and caret transforms.
- After the Unicode editing work, the complete Release and ASan/UBSan
  mutation suites pass 582,689 checks, including maxlength and paste.
- Real Linux X11 input through IBus 1.5.29 / libpinyin 1.15.7 passes nine checks
  with `IBUS_ENABLE_SYNC_MODE=0`: type `nihao`, commit `你好`, compose `中文`,
  cancel it, and undo the earlier result once. Preedit and committed captures
  were visually reviewed with Godot's engine font and Noto CJK fallback.

Standard Godot 4.7.2 has an X11 commit-delivery race. A trace of the intermittent
Weva failure shows Xlib producing a valid committed-text key before preedit
completion; Godot suppresses it without decoding the string. A standalone
native LineEdit, with no Weva extension loaded, reproduces the same loss when
the following protocol polling is delayed by 100 ms. Matched unpatched Godot
builds fail 3/3 delayed sessions. A local patch to Godot's X11 key handler
passes 3/3 sessions for each control, nine checks per session, and both controls
also pass without the injected delay.

IBus 1.5.29's synchronous mode (`IBUS_ENABLE_SYNC_MODE=1`) has a second issue:
its client library does not dispatch queued preedit hide/show messages.
The Godot patch delivers the committed characters, but OS preedit remains
active. A private backport of the handlers present in IBus 1.5.30 makes both
controls pass the synchronous fixture, including commit, cancel and undo.
The matched unpatched IBus library still fails and logs the unsupported hide
message. See [IBus #2585](https://github.com/ibus/ibus/issues/2585).

The source checkout's `godot-port/tools/godot-ime-repro/README.md` includes the
Godot patch, IBus backport, trace tool and exact comparison procedure. These
are local diagnostic builds. The preview addon retains its standard Godot
compatibility declarations and remains exposed to the stock engine's race;
the addon does not modify Godot, system IBus or the user's input environment.
Release work includes landing or otherwise resolving the engine fix and
verifying the intended supported IME configurations.
Actual Windows IME sessions, other input methods, Wayland, macOS and mobile
composition have not been verified. Candidate coordinate transforms are
tested; candidate popup appearance is not covered by the headless fixture.

The C++ test font has no CJK fallback; use Godot's engine font or an appropriate
registered font for these glyphs. Full browser editing remains incomplete,
including bidi behavior and remaining form constraints. Single-line fields now scroll
their text and candidate anchor together; see [TEXT_EDITING.md](TEXT_EDITING.md).
Undo restores the original selection snapshot; Chrome's composition undo
selection endpoint can differ even when the restored value agrees.

## Reproducing the checks

From the repository root:

```powershell
node godot-port/tools/oracle/check_ime_chrome.cjs "C:/Program Files/Google/Chrome/Application/chrome.exe"
```

`check.sh` includes `ime_integration_tests.tscn` alongside native input and
keyboard tests. The real input-method test runs separately on Linux:

```sh
python3 godot-port/hosts/godot/check_ime_linux.py --godot /path/to/godot --artifacts /path/to/logs
```

It requires IBus, libpinyin, Xvfb, xauth, xdotool, Openbox, dbus-run-session
and scrot. It creates a private display, session bus and settings directory,
retains logs and screenshots, and stops only the processes it started.
Add `--ibus-sync-mode 1` to reproduce the IBus 1.5.29 compatibility failure.
`ime_native_probe.tscn` can also run manually with the desktop's own input
method. Add `--native-line-edit` to the Python runner for the native LineEdit
comparison; the fixture disables Weva's input routing in that mode. Probe logs
include native editing/focus state, key Unicode values and frame numbers.
For the comparison with no extension loaded, point `--project` at
`godot-port/tools/godot-ime-repro` and add `--native-line-edit`. Optional
`--trace-library`, `--commit-gap-ms` and `--xim` arguments support the isolated
engine/input-method comparisons described in that project's README.
