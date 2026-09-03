extends Node2D

# A worked example of driving a Weva document from GDScript.
#
# The script holds game state and nothing else. The markup says where each
# piece of it is shown, which class goes on when, what a button is for, and how
# a list is laid out -- so all of that can be moved around without the script
# hearing about it. That is the whole argument for writing a UI in HTML.
#
#   godot --path project --scene res://demo.tscn
#
# Try: click the buttons, tab through the controls, type in the name field and
# select some of it, open the quality dropdown, scroll the log with the wheel
# or drag its bar. Ctrl+A selects, Ctrl+C copies. Escape quits.

const HTML := """
<div class='panel'>
  <div class='row'>
    <span class='label'>Health</span>
    <span class='value'>{{ Hp }} / 100</span>
  </div>
  <div class='bar'>
    <div class='fill' style='width: {{ Hp }}%'
         data-class-hurt='Hurt' data-class-low='Low'></div>
  </div>

  <!-- `title` draws a tooltip after the pointer rests on it. One attribute,
       no script. -->
  <div class='row'>
    <button on-click='take_damage' title='Lose 10 health'>Take 10</button>
    <button on-click='heal' data-class-off='AtFullHealth'
            title='Recover 10, up to 100'>Heal 10</button>
    <button class='ghost' on-click='confirm_revive' data-class-off='Alive'
            title='Back to full health'>Revive</button>
  </div>

  <!-- `on-change`, not `on-input`: this fires ONCE, when the focus leaves a
       field whose value moved. `on-input` fires per keystroke, which is what
       you want for a live filter and not for anything that costs something. -->
  <div class='row'>
    <span class='label'>Name</span>
    <input id='name' type='text' value='Vintner of Halden' on-change='rename'>
  </div>

  <div class='row'>
    <span class='label'>Quality</span>
    <select id='quality' on-input='set_quality'>
      <option value='low'>Low</option>
      <option value='med' selected>Medium</option>
      <option value='high'>High</option>
      <option value='ultra'>Ultra</option>
    </select>
    <label class='check'><input id='shield' type='checkbox' on-input='toggle_shield'> Shield</label>
  </div>

  <!-- One row per entry, straight from the data. Wheel over it, drag its bar,
       or press Page Down. Click a row: `row_activated` says WHICH one, by the
       `data-key` field, so the script never has to thread an id through the
       markup. -->
  <div id='log' class='log'>
    <template data-each='Log as entry' data-key='Id'>
      <div class='entry' on-click='inspect'
           data-class-good='entry.Good' data-class-hurt='entry.Bad'>
        {{ entry.Text }}
      </div>
    </template>
  </div>

  <div class='status'>{{ Status }}</div>

  <!-- A modal dialog. `show_modal_dialog` dims what is behind it with a
       `::backdrop`; a plain `show_dialog` would not. -->
  <dialog id='confirm'>
    <p>Revive to full health?</p>
    <div class='row'>
      <button on-click='do_revive'>Yes</button>
      <button class='ghost' on-click='dismiss'>Cancel</button>
    </div>
  </dialog>
</div>
"""

# The animation is CSS's job, not the script's. The script sets a number; the
# transition, the pulse and the focus ring come from the stylesheet.
const CSS := """
html, body { margin: 0; background: #12151c; color: #e6edf3;
             font-family: sans-serif; font-size: 14px }
.panel { margin: 24px; padding: 20px; width: 420px;
         background: #171b23; border: 1px solid #262c36; border-radius: 12px }
.row { display: flex; align-items: center; gap: 10px; margin-bottom: 14px }
.label { width: 70px; color: #8b949e }
.value { margin-left: auto; font-variant-numeric: tabular-nums }
.check { display: flex; align-items: center; gap: 6px; margin-left: auto }

.bar { height: 14px; background: #0d1117; border-radius: 7px;
       overflow: hidden; margin-bottom: 18px }
.fill { height: 14px; border-radius: 7px;
        background: linear-gradient(90deg, #2ea043, #3fb950);
        transition: width 260ms ease-out, background 260ms linear }
.fill.hurt { background: linear-gradient(90deg, #bb2d3b, #f85149) }
.fill.low  { animation: pulse 900ms ease-in-out infinite alternate }
@keyframes pulse { 0% { opacity: 1 } 100% { opacity: 0.45 } }

button { padding: 7px 14px; border: 1px solid #30363d; border-radius: 7px;
         background: #21262d; color: #e6edf3; cursor: pointer;
         transition: background 120ms linear, border-color 120ms linear }
button:hover  { background: #30363d; border-color: #8b949e }
button:active { background: #161b22 }
button.ghost  { background: transparent }
/* `data-class-off` is bound to the INVERSE of what enables the button, so the
   markup carries the rule and the script just reports the state. */
button.off { opacity: 0.4; border-color: #21262d; pointer-events: none }

input[type=text], textarea { background: #0d1117; color: #e6edf3;
                             border: 1px solid #30363d; border-radius: 6px;
                             transition: border-color 120ms linear }
input[type=text] { margin-left: auto; width: 220px; padding: 6px 8px }
input:placeholder-shown { color: #6e7681 }
input[type=checkbox]:checked { accent-color: #3fb950 }

select { width: 130px; height: 30px; padding: 4px 8px; background: #21262d;
         color: #e6edf3; border: 1px solid #30363d; border-radius: 6px;
         cursor: pointer }

.log { height: 96px; overflow-y: auto; margin-bottom: 14px;
       padding: 6px 8px; background: #0d1117; border-radius: 6px;
       scrollbar-color: #30363d transparent }
.entry { padding: 3px 0; color: #8b949e; font-size: 12px;
         border-bottom: 1px solid #161b22 }
.entry.hurt { color: #f85149 }
.entry.good { color: #3fb950 }

.status { padding: 8px 10px; border-radius: 6px; background: #0d1117;
          color: #8b949e; font-size: 12px }

.entry { cursor: pointer }
.entry:hover { background: #161b22; color: #e6edf3 }

/* The dialog's own frame. `::backdrop` is the dim behind a MODAL one -- a
   plain `show_dialog` never creates it. */
dialog { padding: 20px; border: 1px solid #30363d; border-radius: 10px;
         background: #171b23; color: #e6edf3 }
dialog p { margin: 0 0 14px 0 }
::backdrop { background: rgba(0, 0, 0, 0.6) }
"""

const MAX_ENTRIES := 40

var _doc: WevaDocument
var _hp := 100
var _log: Array = []
var _next_id := 0
var _status := "ready"


func _ready() -> void:
	_doc = WevaDocument.new()
	_doc.document_size = get_viewport_rect().size
	_doc.css = CSS
	_doc.html = HTML
	add_child(_doc)

	# Two lines of wiring. `on-click` in the markup names the method; this says
	# which object has it. Nothing else connects a button to anything.
	_doc.set_controller(self)
	_doc.value_changed.connect(_on_value_changed)
	# Which row a click landed in, by the `data-key` field. The script never
	# sees an element id -- the rows have none, and do not need any.
	_doc.row_activated.connect(_on_row_activated)

	_say("ready")


# ---- what the markup reads ------------------------------------------------
#
# One place that turns game state into the shape the document asks for. Nothing
# here knows WHERE any of it is shown.
func _push() -> void:
	_doc.data = {
		"Hp": _hp,
		"Hurt": _hp <= 50,
		"Low": _hp <= 20,
		"Alive": _hp > 0,
		"AtFullHealth": _hp >= 100,
		"Status": _status,
		"Log": _log,
	}


func _say(message: String) -> void:
	_status = message
	_push()


func _log_entry(text: String, kind: String) -> void:
	_next_id += 1
	_log.append({
		"Id": _next_id,
		"Text": text,
		"Good": kind == "good",
		"Bad": kind == "bad",
	})
	# A bounded log drops from the front. `data-key` means the rows that stay
	# are the same rows, so a scroll or a selection inside one survives.
	while _log.size() > MAX_ENTRIES:
		_log.pop_front()
	_push()
	_doc.scroll_into_view("#log > .entry:last-child")


# ---- what the buttons call ------------------------------------------------
#
# Named by `on-click` in the markup. The id of the element that was clicked
# arrives as the argument, unused here because the method name already says
# which one it was.
func take_damage(_id: String) -> void:
	_hp = max(0, _hp - 10)
	_log_entry("took 10 damage (%d hp)" % _hp, "bad")
	_say("%d hp" % _hp)


func heal(_id: String) -> void:
	_hp = min(100, _hp + 10)
	_log_entry("healed 10 (%d hp)" % _hp, "good")
	_say("%d hp" % _hp)


# Asking, rather than doing. The dialog is markup; this only opens it.
func confirm_revive(_id: String) -> void:
	_doc.show_modal_dialog("#confirm")


func do_revive(_id: String) -> void:
	_doc.close_dialog("#confirm")
	_hp = 100
	_log_entry("revived", "good")
	_say("revived")


func dismiss(_id: String) -> void:
	_doc.close_dialog("#confirm")


# Named by `on-click` on the row. The id is empty -- a repeated row has none --
# so the row's identity arrives through `row_activated` below instead.
func inspect(_id: String) -> void:
	pass


func _on_row_activated(handler: String, index: int, key: String) -> void:
	if handler != "inspect":
		return
	# `key` is the entry's `Id`, so it names the same entry however the list is
	# sorted or trimmed -- which `index` alone would not.
	for entry in _log:
		if str(entry["Id"]) == key:
			_say("entry %d: %s" % [index + 1, entry["Text"]])
			return


func rename(_id: String) -> void:
	_say("name is now '%s'" % _doc.get_element_value("#name"))


func set_quality(_id: String) -> void:
	var quality := _doc.get_element_value("#quality")
	_log_entry("quality set to %s" % quality, "")
	_say("quality: %s" % quality)


func toggle_shield(_id: String) -> void:
	var up := _doc.get_element_value("#shield") == "on"
	_log_entry("shield " + ("up" if up else "down"), "good" if up else "")


# A control with no `on-input` of its own still reports here, which is the
# fallback when the markup names nothing.
func _on_value_changed(id: String, value: String) -> void:
	if id.is_empty():
		_say("something changed to '%s'" % value)


func _unhandled_input(event: InputEvent) -> void:
	if not (event is InputEventKey and event.pressed):
		return
	match event.keycode:
		KEY_ESCAPE:
			get_tree().quit()
		KEY_A when event.ctrl_pressed:
			# The ABI's key enum has no letters, so the document never sees
			# Ctrl+A. Select-all is the host's to trigger.
			_doc.select_all()
		KEY_C when event.ctrl_pressed:
			var selected := _doc.get_selected_text()
			if not selected.is_empty():
				DisplayServer.clipboard_set(selected)
				_say("copied %d characters" % selected.length())
		KEY_V when event.ctrl_pressed:
			_doc.send_text(DisplayServer.clipboard_get())
