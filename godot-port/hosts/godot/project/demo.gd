extends Node2D

# A worked example of driving a Weva document from GDScript.
#
# Everything a game needs is here and nothing else: read and write what the
# document says, toggle a class to restyle it, BUILD a list from game state,
# offer a choice, hear about clicks and edits, and let CSS do the animation.
#
#   godot --path project --scene res://demo.tscn
#
# Try: click the buttons, tab through the controls, type in the name field and
# select some of it, open the quality dropdown, scroll the log with the wheel
# or drag its bar. Ctrl+C copies the selection. Escape quits.

const HTML := """
<div class='panel'>
  <div class='row'>
    <span class='label'>Health</span>
    <span id='hp-text' class='value'>100 / 100</span>
  </div>
  <div class='bar'><div id='hp-fill' class='fill'></div></div>

  <div class='row'>
    <button id='hit'>Take 10</button>
    <button id='heal'>Heal 10</button>
    <button id='revive' class='ghost'>Revive</button>
  </div>

  <div class='row'>
    <span class='label'>Name</span>
    <input id='name' type='text' value='Vintner of Halden' placeholder='name'>
  </div>

  <div class='row'>
    <span class='label'>Quality</span>
    <select id='quality'>
      <option value='low'>Low</option>
      <option value='med' selected>Medium</option>
      <option value='high'>High</option>
      <option value='ultra'>Ultra</option>
    </select>
    <label class='check'><input id='shield' type='checkbox'> Shield</label>
  </div>

  <!-- Empty on purpose: the log is built from what happens, not written
       here. Wheel over it, drag its bar, or press Page Down. -->
  <div id='log' class='log'></div>

  <div class='row'>
    <span class='label'>Notes</span>
  </div>
  <textarea id='notes' placeholder='anything worth remembering'></textarea>

  <div id='status' class='status'>ready</div>
</div>
"""

# The animation is CSS's job, not the script's. The script sets a width and a
# class; the transition, the pulse and the focus ring come from the stylesheet,
# which is the whole point of driving a UI this way.
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
.fill { height: 14px; width: 100%; border-radius: 7px;
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
/* A disabled control takes no pointer events at all -- no hover, no click --
   so this is the whole of what `disabled` needs from the stylesheet. */
button:disabled { opacity: 0.4; border-color: #21262d }

input[type=text], textarea { background: #0d1117; color: #e6edf3;
                             border: 1px solid #30363d; border-radius: 6px;
                             transition: border-color 120ms linear }
input[type=text] { margin-left: auto; width: 220px; padding: 6px 8px }
textarea { display: block; width: 380px; height: 56px; padding: 6px 8px;
           font-size: 13px; line-height: 18px; margin-bottom: 14px }
input:placeholder-shown, textarea:placeholder-shown { color: #6e7681 }
input[type=checkbox]:checked { accent-color: #3fb950 }

select { margin-left: 0; width: 130px; height: 30px; padding: 4px 8px;
         background: #21262d; color: #e6edf3; border: 1px solid #30363d;
         border-radius: 6px; cursor: pointer }

/* A fixed height and `overflow-y: auto` is the whole of a scrollable panel:
   the wheel, the bar, the keyboard and a finger drag all follow from it. */
.log { height: 96px; overflow-y: auto; margin-bottom: 14px;
       padding: 6px 8px; background: #0d1117; border-radius: 6px;
       scrollbar-color: #30363d transparent }
.entry { padding: 3px 0; color: #8b949e; font-size: 12px;
         border-bottom: 1px solid #161b22 }
.entry.hurt { color: #f85149 }
.entry.good { color: #3fb950 }

.status { padding: 8px 10px; border-radius: 6px; background: #0d1117;
          color: #8b949e; font-size: 12px }
"""

const MAX_ENTRIES := 40

var _doc: WevaDocument
var _hp := 100


func _ready() -> void:
	_doc = WevaDocument.new()
	_doc.document_size = get_viewport_rect().size
	_doc.css = CSS
	_doc.html = HTML
	add_child(_doc)

	# Five lines is the whole wiring. The document reports which element was
	# used by its `id`, which is the name the stylesheet already knows it by.
	_doc.element_clicked.connect(_on_clicked)
	_doc.value_changed.connect(_on_value_changed)
	_doc.text_entered.connect(func(id, _text): _say("typing in %s" % id))

	_refresh()
	_log("ready", "")


func _on_clicked(id: String) -> void:
	match id:
		"hit":
			_hp = max(0, _hp - 10)
			_log("took 10 damage (%d hp)" % _hp, "hurt")
		"heal":
			_hp = min(100, _hp + 10)
			_log("healed 10 (%d hp)" % _hp, "good")
		"revive":
			_hp = 100
			_log("revived", "good")
		_: return
	_say("%s -> %d hp" % [id, _hp])
	_refresh()


func _on_value_changed(id: String, value: String) -> void:
	match id:
		"shield":
			var up := value == "on"
			_say("shield " + ("up" if up else "down"))
			_log("shield " + ("up" if up else "down"), "good" if up else "")
		"name":
			_say("name is now '%s'" % value)
		"quality":
			# A <select> reports the chosen option's value, which is where the
			# DOM keeps it -- there is no separate state to hold in step.
			_say("quality: %s" % value)
			_log("quality set to %s" % value, "")


# One place that maps game state onto the document. The bar does not jump: the
# stylesheet has a transition on `width`, so setting it once animates it.
func _refresh() -> void:
	_doc.set_element_text("#hp-text", "%d / 100" % _hp)
	_doc.set_element_attribute("#hp-fill", "style", "width: %d%%" % _hp)
	_doc.toggle_element_class("#hp-fill", "hurt", _hp <= 50)
	_doc.toggle_element_class("#hp-fill", "low", _hp <= 20)

	# `disabled` is a DOM attribute like any other, and the document stops
	# sending the control anything -- no hover, no click -- while it is set.
	if _hp >= 100:
		_doc.set_element_attribute("#heal", "disabled", "")
	else:
		_doc.remove_element_attribute("#heal", "disabled")
	if _hp > 0:
		_doc.set_element_attribute("#revive", "disabled", "")
	else:
		_doc.remove_element_attribute("#revive", "disabled")


# The log is the part that cannot be written as markup in advance: its length
# is the game's business. A row is appended, the oldest are dropped, and the
# newest is scrolled to -- which is what every chat pane and quest log does.
func _log(message: String, kind: String) -> void:
	var classes := "entry" if kind.is_empty() else "entry " + kind
	_doc.append_html("#log", "<div class='%s'>%s</div>" % [classes, message.xml_escape()])
	while _doc.count_elements("#log .entry") > MAX_ENTRIES:
		_doc.remove_element("#log .entry:nth-child(1)")
	# Rows are addressed the way CSS addresses them, so the newest is simply
	# the last child.
	_doc.scroll_into_view("#log .entry:last-child")


func _say(message: String) -> void:
	_doc.set_element_text("#status", message)


func _unhandled_input(event: InputEvent) -> void:
	if not (event is InputEventKey and event.pressed):
		return
	match event.keycode:
		KEY_ESCAPE:
			get_tree().quit()
		KEY_A when event.ctrl_pressed:
			# The ABI's key enum has no letters, so the document never sees
			# Ctrl+A. Select-all is the host's to trigger, and this is it.
			_doc.select_all()
		KEY_C when event.ctrl_pressed:
			# And the clipboard belongs to the platform, so copying is two
			# lines here rather than a feature in the engine.
			var selected := _doc.get_selected_text()
			if not selected.is_empty():
				DisplayServer.clipboard_set(selected)
				_say("copied %d characters" % selected.length())
		KEY_V when event.ctrl_pressed:
			_doc.send_text(DisplayServer.clipboard_get())
