extends Node2D

# A worked example of driving a Weva document from GDScript.
#
# Everything a game needs is here and nothing else: read and write what the
# document says, toggle a class to restyle it, BUILD a list from game state,
# hear about clicks and edits, and let CSS do the animation. Open demo.tscn and
# use it.
#
#   godot --path project --scene res://demo.tscn

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
    <label><input id='shield' type='checkbox'> Shield</label>
    <input id='name' type='text' value='Vintner' placeholder='name'>
  </div>

  <!-- Empty on purpose: the log is built from what happens, not written
       here. Wheel over it, drag its bar, or press Page Down. -->
  <div id='log' class='log'></div>

  <div id='status' class='status'>ready</div>
</div>
"""

# The animation is CSS's job, not the script's. The script sets a width and a
# class; the transition and the pulse come from the stylesheet, which is the
# whole point of driving a UI this way.
const CSS := """
html, body { margin: 0; background: #12151c; color: #e6edf3;
             font-family: sans-serif; font-size: 14px }
.panel { margin: 24px; padding: 20px; width: 420px;
         background: #171b23; border: 1px solid #262c36; border-radius: 12px }
.row { display: flex; align-items: center; gap: 10px; margin-bottom: 14px }
.label { width: 70px; color: #8b949e }
.value { margin-left: auto; font-variant-numeric: tabular-nums }

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

input[type=text] { margin-left: auto; width: 130px; padding: 6px 8px;
                   background: #0d1117; color: #e6edf3;
                   border: 1px solid #30363d; border-radius: 6px;
                   transition: border-color 120ms linear }
input[type=text]:focus { border-color: #388bfd }
input:placeholder-shown { color: #6e7681 }
input[type=checkbox]:checked { accent-color: #3fb950 }

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
	if id == "shield":
		_say("shield " + ("up" if value == "on" else "down"))
		_log("shield " + ("up" if value == "on" else "down"), "")
	elif id == "name":
		_say("name is now '%s'" % value)


# One place that maps game state onto the document. The bar does not jump: the
# stylesheet has a transition on `width`, so setting it once animates it.
func _refresh() -> void:
	_doc.set_element_text("#hp-text", "%d / 100" % _hp)
	_doc.set_element_attribute("#hp-fill", "style", "width: %d%%" % _hp)
	_doc.toggle_element_class("#hp-fill", "hurt", _hp <= 50)
	_doc.toggle_element_class("#hp-fill", "low", _hp <= 20)


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
	if event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		get_tree().quit()
