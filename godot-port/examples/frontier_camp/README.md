# Frontier Camp — standalone Godot integration

Installed Windows addon: **preview211 (ABI minor24)**; `addons/weva/build.json` records the
installed version. Sample, exports and desktop
performance checks pass; [qualification and remaining scope](../../docs/PRODUCT_READINESS.md).


Open `project.godot` in the verified patched **Godot 4.7.2** editor described in
the [engine guide](../../docs/GODOT_TEXT_SHAPING.md#windows-patched-engine-bundle-2026-09-08), then press **F5**. The prepared local
project has the Windows addon installed. This is independent of the host gallery.

Use the same bundle's debug and release binaries as the Windows export preset's
**Custom Template** paths. The project already includes ICU support data in
exports (`internationalization/locale/include_text_server_data=true`). The
addon shapes long emoji-heavy text in pieces, so this sample's long-Unicode
name churn also completes on the official stock 4.7.1 editor; the engine's own
defect still affects native Godot controls, see
[text shaping](../../docs/GODOT_TEXT_SHAPING.md).

For a fresh checkout, extract a current addon ZIP here, or run:

```sh
python install_addon.py /path/to/weva-preview.zip
```

Open the editor once to discover the extension and import artwork. `addons/`
and `.godot/` are local/generated. No files outside the project are used at
runtime. The addon must contain `weva_view.gd` and your platform's library.

## Try it

- **Gather supplies** or **E** adds wood, spends stamina and updates the HUD.
- **Craft campfire** unlocks with 4 wood and 2 stone. Craft it, reverse the
  satchel order, then **Place** the kit. Actions follow item identity.
- **H** takes damage. Use a bandage; the final item removes its row.
- **Settings** edits the traveler name, volume and audio enable state through
  two-way binding. Volume/mute apply to Godot's Master audio bus. **Restore
  defaults** uses HTML form reset. **Escape** returns to camp.
- Click open ground between the panels and HUD to forage through gameplay
  input. UI clicks and typing do not also trigger game actions.
- A controller works too with preview219 or later (`game.gd` opts into
  `gamepad_wake`): the first press wakes the UI on **Settings**, A opens it,
  the pad moves between rows and adjusts the slider, B closes it.
- A native Timer advances the clock and restores stamina once camp is built.

This UI sample uses a static landscape and a small resource/crafting simulation.
Window sizes start at 1024 × 720. No settings are saved; restarting restores
the sample defaults.

## How it connects

| File | Responsibility |
| --- | --- |
| `main.tscn` | Native scene; UI node's `html_file` selected in the Inspector |
| `camp_state.gd` | Game rules, Dictionary and zero-argument `changed` signal |
| `game.gd` | Binding setup, HTML action methods and native audio/input |
| `ui/camp.html` | Text, style, boolean, repeated-row and two-way bindings |
| `ui/camp.css` | Layout, appearance, pointer routing and narrow-window rules |
| `tests/integration.gd` | Native input tests against the actual game scene |

The entire binding setup is:

```gdscript
ui.bind_state(state.model, self, state.changed)
ui.data_changed.connect(_on_data_changed)
```

Game actions mutate `state.model`, then emit `state.changed`. The helper combines
notifications into one deferred refresh, with no idle polling. Game code does
not set labels or call `update_document()`. Before an immediate geometry read,
use `ui.flush_bindings()`.

Changed bindings use incremental text/style updates. Unchanged notifications
do not rebuild or repaint the document. Give HUD labels a stable CSS slot when
their surrounding layout should stay fixed: the traveler name uses `width:
170px` with clipped overflow. Inventory structure changes still rebuild layout;
ordinary count, meter and label updates do not require rebuilding every row.

```html
<strong>{{ Player.Health }}</strong>
<span style="width: {{ Player.Health }}%"></span>
<button id="craft" on-click="craft" disabled="{{ View.CraftDisabled }}">Craft</button>
<input id="player-name" data-model="Player.Name">
```

Bindings are paths, not expressions. Compute derived values in game state.
Templated booleans control attribute presence; a literal `disabled="false"`
still disables a button under HTML rules.

Inventory uses `data-each="Items as item" data-key="Id"`. Controllers call
`ui.get_row("#" + element_id).key`, so sorting/removing rows cannot redirect an
action to the wrong item. Reordering the same unique explicit keys moves the
existing rows and preserves their focus, selection and undo history. Membership
changes, duplicate/missing keys and externally interleaved rows use reconstruction.

Two-way edits update the shared Dictionary before `data_changed` is emitted.
Read the Dictionary for typed ints/bools; the signal's value is control text.

## Repeat the checks

See [PERFORMANCE.md](PERFORMANCE.md) for measured release-build CPU/GPU costs,
the binding-versus-direct comparison, and commands to repeat the benchmarks.

```sh
godot --headless --path . --editor --quit-after 60
godot --headless --path . -- --check
godot --path . --rendering-method gl_compatibility -- --check --capture=/existing/folder/frontier
godot --path . --rendering-method mobile -- --check
```

Tests inject native mouse and keyboard events through Godot's viewport, checking
the UI and game state after each action. They cover deferred refresh, lifecycle,
typing, sliders, checkbox, form reset, gameplay routing, dynamic rows and three
viewport sizes. `--capture` saves camp, crafted inventory and settings PNGs.
The same `--check` argument works on an exported executable.

The repository's `hosts/godot/check_frontier_camp.py` creates a fresh consumer
project from this source and an addon ZIP, tests it, exports a release game,
relocates the export and tests it without the source project. Matching Godot
export templates are required for `--native`.

```sh
python ../../hosts/godot/check_frontier_camp.py --godot /path/to/godot \
  --addon /path/to/weva-preview.zip --output /new/output/folder --native \
  --debug-template /path/to/patched-debug-template \
  --release-template /path/to/patched-release-template
```

Its output contains logs, PNGs, a verification receipt, a complete project ZIP
and the exported game. `export_presets.cfg` includes HTML/CSS and all artwork.

## Prepare a screen during loading and reuse it

Instantiate the screen once while loading the level, prepare its UI while it is
hidden, then keep that instance for later openings:

```gdscript
var screen = load("res://main.tscn").instantiate()
screen.hide()
add_child(screen)
screen.ui.update_document(0) # Parse, bind, lay out and prepare textures now.
# When gameplay needs the screen:
screen.show()
# Close it with screen.hide(); reuse it instead of instantiating another copy.
```

This moves construction into loading time. Hidden preparation still has a CPU
and memory cost, and first rendering can require renderer work. Pause a hidden
view's processing if appropriate, then resume and flush bindings before an
immediate geometry read. Hiding does not discard the shared game model.

The lightweight `boot.tscn` enters `main.tscn` in normal play. Its `--lifecycle`
test entry runs before the first UI is created, so exported cold-construction
measurements do not accidentally reuse a UI already loaded by the main scene.
See [PERFORMANCE.md](PERFORMANCE.md) for release measurements and commands for
3D load, resolution, preparation/reuse and process-memory checks.
Export only to a platform listed in `addons/weva/build.json`. Development preview.

Artwork comes from this repository's western survival sample: generated landscape
and original editable SVG icons, covered by the repository license.

## Chrome parity

The runtime67 direct browser check passes all 1,276 sampled geometry and 33
focus, typing and scroll comparisons. Broad CSS parity remains incomplete. See [CHROME_PARITY.md](CHROME_PARITY.md) for
measurements, remaining defects and the repeatable check.
