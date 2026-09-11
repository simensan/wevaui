# Godot X11 IME commit reproduction

This is a standalone Godot project containing one native `LineEdit`. It has
no Weva class, addon or GDExtension. It reproduces an X11 commit being lost
when the main thread handles it before the input method's preedit ends.

## Reproduce

On Linux x86_64 with glibc, install the test dependencies: IBus, libpinyin,
Xvfb, xauth, xdotool, Openbox, dbus-run-session, scrot and CJK fonts. Building
the optional trace library also requires a C compiler and X11 headers.
From the repository root:

```sh
godot --headless --editor --path godot-port/tools/godot-ime-repro --quit-after 60
cc -shared -fPIC -O2 godot-port/tools/godot-ime-repro/trace_xim.c -ldl -o /path/to/libtrace_xim.so
python3 godot-port/hosts/godot/check_ime_linux.py \
  --godot /path/to/godot \
  --project godot-port/tools/godot-ime-repro --native-line-edit \
  --artifacts /path/to/logs --ibus-sync-mode 0 \
  --trace-library /path/to/libtrace_xim.so --commit-gap-ms 100
```

The runner creates a private display, session bus and IBus settings, types
`nihao`, commits `你好`, starts `中文`, cancels it, and undoes the first commit.
It retains the event report, native logs and screenshots. A successful run
prints `9 checks, 0 failures` and exits zero; a lost commit exits nonzero.
The trace library is preloaded only into the test's Godot process.

`--commit-gap-ms 100` delays subsequent X event polling after Xlib produces
an unfiltered keycode-zero commit. It gives the main thread time to handle
that key while composition is still active. It does not fabricate committed
text, consume protocol packets, or reorder queued events. Without the delay,
the same ordering occurred naturally in an intermittent Weva failure.
Omit the gap to trace ordinary timing, or omit both trace options to run
without interception. This instrumentation logs test keystrokes and is for
isolated diagnostics only.

The root node receives IME notifications before its child LineEdit. The
probe records both notification arrival and deferred control state, avoiding
mistaking that notification order for a stuck preedit.

## Cause and candidate patch

Tested against Godot **4.7.2**, commit
`ed1daf0bf001b61586d9930840f2f1394092c079`, IBus **1.5.29-2**, libpinyin
**1.15.7-1build2**, and libX11 **1.8.7** on Ubuntu 24.04 / Xvfb.

Godot's [`_handle_key_event`](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/platform/linuxbsd/x11/display_server_x11.cpp#L3935)
returns immediately while `ime_in_progress`. Xlib represents an XIM commit
as a synthetic `KeyPress` with keycode zero; its
[`_XimCommitRecv`](https://github.com/mirror/libX11/blob/libX11-1.8.7/modules/im/ximcp/imDefLkup.c)
queues the committed string for `Xutf8LookupString`. If Godot handles this
key before preedit completion, it returns without retrieving the string.

[`godot-x11-ime-commit.patch`](godot-x11-ime-commit.patch) allows these commit
keys through the existing Unicode decoder while retaining suppression of
ordinary keys during preedit. Apply it to that exact Godot source checkout:

```sh
git apply /path/to/unityui/godot-port/tools/godot-ime-repro/godot-x11-ime-commit.patch
scons -j12 platform=linuxbsd target=template_debug optimize=speed \
  debug_symbols=no use_lto=no vulkan=no opengl3=yes wayland=no \
  disable_3d=yes disable_path_overrides=no module_mono_enabled=no
```

Use the resulting `bin/godot.linuxbsd.template_debug.x86_64` as `--godot`.
Import the project first with the official editor, as above. These reduced
build options are for this diagnostic comparison; this is not a replacement
editor or a supported Weva engine distribution. The patch has not been
submitted upstream and does not alter the preview addon's libraries.

## Verification (2026-09-06)

Matched local Godot builds use the same source commit and build options;
their only source difference is the included patch.

| Godot / control | IBus mode | Injected gap | Result |
|---|---|---:|---|
| Official 4.7.2 / standalone LineEdit | asynchronous | 100 ms | Commit lost |
| Matched unpatched build / standalone LineEdit | asynchronous | 100 ms | 3/3 sessions lose commit; no UTF-8 lookup |
| Patched build / standalone LineEdit | asynchronous | 100 ms | 3/3 sessions pass all 9 checks |
| Patched build / Weva | asynchronous | 100 ms | 3/3 sessions pass all 9 checks |
| Patched build / each control | asynchronous | none | Each passes all 9 checks |
| Patched build / each control | synchronous | none | Committed characters arrive; OS preedit never clears |
| Unpatched build / LineEdit, IBus hide/show backport | synchronous | 100 ms | Commit lost despite preedit clearing |
| Patched build / LineEdit, IBus hide/show backport | synchronous | 100 ms | 3/3 sessions pass all 9 checks |
| Patched build / Weva, IBus hide/show backport | synchronous | 100 ms | 3/3 sessions pass all 9 checks |

The synchronous IBus failure is separate. IBus 1.5.29 queues preedit hide/show
messages in synchronous mode but lacks their client-side handlers. This is
documented in [IBus #2585](https://github.com/ibus/ibus/issues/2585); the handlers
appear in [IBus 1.5.30](https://github.com/ibus/ibus/blob/1.5.30/src/ibusinputcontext.c).
The Godot patch does not supply those missing callbacks. A matched private
IBus 1.5.29 library reproduces the failure and logs `Type 'h' is not supported`;
backporting just the hide/show handlers makes synchronous LineEdit and Weva
pass all nine checks. The system IBus installation is unchanged.

To repeat that comparison without installing a replacement library, use the
[official IBus 1.5.29 archive](https://github.com/ibus/ibus/releases/download/1.5.29/ibus-1.5.29.tar.gz)
(SHA-256 `4a457f10e29da0623e96cbbaca89cc529145587141f8e64c305f17dc875d5e6e`)
and [`ibus-1.5.29-preedit.patch`](ibus-1.5.29-preedit.patch). This is a minimal
backport of the handlers present in 1.5.30, whose source is LGPL-2.1-or-later.
Configure in the extracted source directory with GLib/GIO, D-Bus development
headers and the normal Autotools tools installed:

```sh
./configure --prefix=/path/to/private-ibus \
  --disable-gtk2 --disable-gtk3 --disable-gtk4 --disable-wayland --disable-xim \
  --disable-introspection --disable-vala --disable-python-library \
  --disable-dconf --disable-ui --disable-emoji-dict --disable-unicode-dict \
  --disable-nls --disable-appindicator --disable-setup --disable-libnotify \
  --disable-engine --disable-tests
make -C src -j12 libibus-1.0.la
# Retain src/.libs/libibus-1.0.so.5 as a baseline before applying the patch.
patch -p1 < /path/to/unityui/godot-port/tools/godot-ime-repro/ibus-1.5.29-preedit.patch
make -C src -j12 libibus-1.0.la
```

Create an executable wrapper that sets `LD_LIBRARY_PATH` to the absolute
patched `src/.libs` directory and then `exec /usr/libexec/ibus-x11` (adjust
the executable path for the distribution). Add `--xim /path/to/wrapper
--ibus-sync-mode 1` to the Python test. The runner starts that XIM process
inside its private display/session and records `xim.log`. Only that process
uses the selected IBus library; no `make install` or desktop language change
is needed. Use the patched Godot build to isolate the IBus result.

These results cover the stated X11 stack and Pinyin sequence. They do not
establish compatibility with other IMEs, Wayland, Windows or macOS. See
[IME.md](../../docs/IME.md) for the addon API and remaining release work.
