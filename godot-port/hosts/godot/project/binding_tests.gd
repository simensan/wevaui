extends Node2D

# Two-way binding, from GDScript, the way a game would use it.
#
# One-way binding has its own coverage: `{{ path }}` in text and in attributes
# is exercised by the demo and by the C ABI tests. What is asserted here is the
# RETURN path -- `data-model` on a control, the user changing it, and the value
# arriving back in the script's own dictionary with the type it started with.

var failures := 0
var checks := 0

func _check(condition: bool, description: String) -> void:
	checks += 1
	if not condition:
		failures += 1
		printerr("FAIL  ", description)

const HTML := """
<div class="panel">
  <input id="name" type="text" data-model="Player.Name">
  <input id="volume" type="range" min="0" max="100" data-model="Settings.Volume">
  <input id="music" type="checkbox" data-model="Settings.Music">
  <p id="echo">{{ Player.Name }} at {{ Settings.Volume }}</p>
  <input id="loose" type="text" data-model="Fresh.Field">
  <input id="plain" type="text" value="untouched">
</div>
"""

const ROWS_HTML := """
<ul id="quests">
  <template data-each="Quests as quest" data-key="Id">
    <li class="row">
      <span>{{ quest.Title }}</span>
      <input type="text" data-model="quest.Note">
    </li>
  </template>
</ul>
"""

# The three gestures the write-back is reached through, as a player makes them.

func _type_into(doc: WevaDocument, selector: String, text: String) -> void:
	doc.set_focus(selector)
	doc.update_document()
	doc.select_all()
	doc.send_text(text)
	doc.update_document()

func _click(doc: WevaDocument, selector: String) -> void:
	var box := doc.query_bounds(selector)
	var at := box.position + box.size * 0.5
	doc.set_pointer(at, 1)
	doc.update_document()
	doc.set_pointer(at, 0)
	doc.update_document()

func _drag_range(doc: WevaDocument, selector: String, fraction: float) -> void:
	var box := doc.query_bounds(selector)
	var at := Vector2(box.position.x + box.size.x * fraction,
			box.position.y + box.size.y * 0.5)
	doc.set_pointer(at, 1)
	doc.update_document()
	doc.set_pointer(at, 0)
	doc.update_document()

# Reading the document back: what the cascade decided, and every match of a
# selector rather than only the first.
func _reading_back(_unused: WevaDocument) -> void:
	var doc := WevaDocument.new()
	add_child(doc)
	doc.document_size = Vector2(600, 400)
	doc.css = """
	.tile { color: rgb(10, 20, 30); font-size: 21px; }
	#dim { display: none; }
	.panel { color: rgb(200, 100, 50); }
	"""
	doc.html = """
	<div class="panel">
	  <span class="tile" id="one">alpha</span>
	  <span class="tile" id="two">beta</span>
	  <span class="tile">gamma</span>
	  <span id="dim">hidden</span>
	  <span class="inherits">delta</span>
	</div>
	"""
	doc.update_document()

	# What the stylesheet decided, which no other call reports.
	_check(doc.get_computed_style("#one", "font-size") == "21px", "a computed length reads back")
	_check(doc.get_computed_style("#dim", "display") == "none", "and a keyword")

	# Computed, not declared: `.inherits` sets no colour of its own and answers
	# with the panel's, because that is the value it is actually using.
	# Against the VALUE as well as against the panel: comparing the two alone
	# passed with the whole feature disabled, because "" equals "".
	var inherited := doc.get_computed_style(".inherits", "color")
	_check(inherited.contains("200"), "an unset inherited property answers with a real value")
	_check(inherited == doc.get_computed_style(".panel", "color"),
			"and it is the one it inherited")

	# A property the element never set and nothing above it set either still
	# answers -- with the initial value.
	_check(doc.get_computed_style("#one", "position") == "static",
			"and an unset non-inherited one with its initial value")
	_check(doc.get_computed_style("#one", "not-a-property") == "",
			"an unknown property is empty rather than a guess")
	_check(doc.get_computed_style("#nothing", "color") == "", "so is a selector that matches nothing")

	# Every match, not just the first.
	var texts := doc.query_all_text(".tile")
	_check(texts.size() == 3, "query_all_text sees every match")
	if texts.size() == 3:
		_check(texts[0] == "alpha" and texts[2] == "gamma", "in document order")

	var boxes := doc.query_all_bounds(".tile")
	_check(boxes.size() == 3, "and so does query_all_bounds")
	if boxes.size() == 3:
		_check(boxes[0].position.x < boxes[1].position.x, "with the boxes laid out across")
		_check(boxes[0].size.x > 0, "and given real widths")

	var ids := doc.query_all_ids(".tile")
	_check(ids.size() == 3, "query_all_ids answers for every match")
	if ids.size() == 3:
		_check(ids[0] == "one" and ids[2] == "", "naming the ones that have an id")

	_check(doc.query_all_text(".nothing-here").size() == 0, "no match is an empty list")
	doc.queue_free()

# One inline declaration at a time. Setting the whole `style` attribute is what
# a script had to do to change one property, and getting that splice wrong
# loses every other declaration on the element.
func _inline_styles() -> void:
	var doc := WevaDocument.new()
	add_child(doc)
	doc.document_size = Vector2(400, 300)
	doc.css = ".panel { color: rgb(1, 2, 3); }"
	doc.html = """
	<div id="a" class="panel" style="width: 100px; height: 40px; color: rgb(9, 9, 9)">a</div>
	<div id="b" class="panel">b</div>
	<div id="c" style="background-image: url(x;y.png); width: 50px">c</div>
	"""
	doc.update_document()

	_check(doc.get_element_style("#a", "width") == "100px", "an inline value reads back")
	_check(doc.get_element_style("#a", "margin") == "", "and one that is not set is empty")

	# Changing one leaves the others alone, which is the whole point.
	doc.set_element_style("#a", "width", "200px")
	doc.update_document()
	_check(doc.get_element_style("#a", "width") == "200px", "setting one changes it")
	_check(doc.get_element_style("#a", "height") == "40px", "and leaves its neighbours")
	_check(doc.query_bounds("#a").size.x == 200, "and layout follows")

	# Adding a property the element did not have.
	doc.set_element_style("#b", "width", "120px")
	doc.update_document()
	_check(doc.query_bounds("#b").size.x == 120, "a property can be added")

	# Removing hands the property back to the stylesheet rather than blanking
	# it: #a's class says rgb(1,2,3) and its inline style overrode it.
	_check(doc.get_computed_style("#a", "color").contains("9"), "inline wins while it is set")
	doc.set_element_style("#a", "color", "")
	doc.update_document()
	_check(doc.get_element_style("#a", "color") == "", "removing clears the declaration")
	_check(doc.get_computed_style("#a", "color").contains("1"),
			"and the stylesheet takes it back")

	# A semicolon inside url() is part of the value. Splitting on every
	# semicolon would truncate it and leave `y.png)` behind as a declaration.
	_check(doc.get_element_style("#c", "background-image") == "url(x;y.png)",
			"a semicolon inside url() is not a separator")
	doc.set_element_style("#c", "width", "80px")
	doc.update_document()
	_check(doc.get_element_style("#c", "background-image") == "url(x;y.png)",
			"and survives a write to another property")

	doc.queue_free()

# An element's box in the space a sibling Node2D lives in.
func _screen_rects() -> void:
	var stage := Node2D.new()
	add_child(stage)
	var doc := WevaDocument.new()
	doc.document_size = Vector2(400, 300)
	doc.html = "<div id='t' style='width: 100px; height: 50px; margin: 20px'>t</div>"
	stage.add_child(doc)
	doc.update_document()

	var local := doc.query_bounds("#t")
	_check(local.position == Vector2(20, 20), "the document box is in document space")

	# Untransformed, the two agree.
	_check(doc.get_element_screen_rect("#t") == local, "and matches the screen rect at 1:1")

	# Scaled and offset -- the way a gallery or a scaled HUD shows a document.
	# A caller anchoring a portrait over a panel needs THIS, and composing it
	# by hand is the mistake the node already made once for hit testing.
	doc.scale = Vector2(0.5, 0.5)
	doc.position = Vector2(100, 60)
	var screen := doc.get_element_screen_rect("#t")
	_check(screen.position == Vector2(110, 70), "a scale and offset are applied")
	_check(screen.size == Vector2(50, 25), "including to the size")

	# A transform on an ancestor counts too.
	doc.scale = Vector2.ONE
	doc.position = Vector2.ZERO
	stage.position = Vector2(7, 9)
	_check(doc.get_element_screen_rect("#t").position == Vector2(27, 29),
			"and an ancestor's transform")

	_check(doc.get_element_screen_rect("#nothing") == Rect2(), "a miss is an empty rect")
	doc.queue_free()

# Focus by DIRECTION, which is the one a gamepad asks and the tab order cannot
# answer: what is to the left of this? In a grid, source order says "the
# previous one", which at the end of every row is the element above-right.
func _gamepad_focus() -> void:
	var doc := WevaDocument.new()
	add_child(doc)
	doc.document_size = Vector2(400, 300)
	doc.css = """
	#grid { display: grid; grid-template-columns: repeat(3, 80px); gap: 10px; }
	button { width: 80px; height: 40px; }
	"""
	doc.html = """
	<div id="grid">
	  <button id="a">a</button><button id="b">b</button><button id="c">c</button>
	  <button id="d">d</button><button id="e">e</button><button id="g">g</button>
	  <button id="h">h</button><button id="i">i</button><button id="j">j</button>
	</div>
	"""
	doc.update_document()

	#   a b c
	#   d e g
	#   h i j
	doc.set_focus("#e")
	_check(doc.focus_move(Vector2.LEFT) == "d", "left of the middle")
	doc.set_focus("#e")
	_check(doc.focus_move(Vector2.RIGHT) == "g", "right of it")
	doc.set_focus("#e")
	_check(doc.focus_move(Vector2.UP) == "b", "above it")
	doc.set_focus("#e")
	_check(doc.focus_move(Vector2.DOWN) == "i", "below it")

	# Straight down a column rather than diagonally to whatever is nearest.
	doc.set_focus("#a")
	_check(doc.focus_move(Vector2.DOWN) == "d", "down a column, not across")

	# At the edge it stays: a menu that wraps under a held stick is worse than
	# one that stops.
	doc.set_focus("#a")
	_check(doc.focus_move(Vector2.UP) == "a", "the top edge holds")
	_check(doc.focus_move(Vector2.LEFT) == "a", "and the left one")

	# An analogue stick rarely reads exactly 1.0; only the sign is used.
	doc.set_focus("#e")
	_check(doc.focus_move(Vector2(0.37, 0)) == "g", "a partial stick still moves")

	doc.queue_free()

func _ready() -> void:
	var doc := WevaDocument.new()
	add_child(doc)
	doc.document_size = Vector2(600, 400)
	doc.html = HTML
	doc.data = {
		"Player": {"Name": "Ada"},
		"Settings": {"Volume": 40, "Music": true},
	}
	doc.update_document()

	# Data -> control. The dictionary reaches the fields without the markup
	# having to repeat itself in a `value` attribute.
	_check(doc.get_element_value("#name") == "Ada", "a model fills its text field")
	_check(doc.get_element_value("#volume") == "40", "and its range")
	_check(doc.query_text("#echo").contains("Ada"), "one-way binding still works beside it")

	# Data -> control, again, after the script changes its mind.
	doc.data = {
		"Player": {"Name": "Grace"},
		"Settings": {"Volume": 75, "Music": true},
	}
	doc.update_document()
	_check(doc.get_element_value("#name") == "Grace", "a new dictionary reaches the field")
	_check(doc.query_text("#echo").contains("75"), "and the text beside it")

	# Control -> data. This is the direction that did not exist.
	#
	# Driven as a USER, not with set_element_value: a script setting a value
	# raises no input event, in this engine as in a browser, so a test that
	# drove it that way would be asserting on a path no player takes.
	var seen := []
	doc.data_changed.connect(func(path, value): seen.append([path, value]))
	_type_into(doc, "#name", "Hopper")
	_check(doc.data["Player"]["Name"] == "Hopper", "typing writes back into the data")
	_check(seen.size() > 0, "and reports the change")
	if seen.size() > 0:
		_check(seen[0][0] == "Player.Name", "naming the path, not the element")

	# The type at the path wins. A script that put an int in the dictionary
	# keeps getting an int, so its own arithmetic survives the first drag.
	_drag_range(doc, "#volume", 0.62)
	_check(typeof(doc.data["Settings"]["Volume"]) == TYPE_INT, "a range keeps the type it had")
	# Against the CONTROL, not a constant: `> 40` was still true of the 75 the
	# script had set, so it passed with the write-back removed entirely.
	_check(str(doc.data["Settings"]["Volume"]) == doc.get_element_value("#volume"),
			"and the data holds what the control now shows")
	_check(doc.data["Settings"]["Volume"] != 75, "which is not what the script last set")

	_click(doc, "#music")
	_check(typeof(doc.data["Settings"]["Music"]) == TYPE_BOOL, "a checkbox stays a bool")
	_check(doc.data["Settings"]["Music"] == false, "and the click cleared it")

	# Everything else bound to the path follows it, without the script asking.
	_check(doc.query_text("#echo").contains("Hopper"), "the label follows the field")
	_check(doc.query_text("#echo").contains("62"), "and so does the number")

	# A path the data does not have yet is made, not dropped on the floor.
	_type_into(doc, "#loose", "made up")
	_check(doc.data.has("Fresh"), "a missing branch is created")
	if doc.data.has("Fresh"):
		_check(doc.data["Fresh"]["Field"] == "made up", "and the leaf written into it")

	# A control with no model is left alone in both directions.
	_check(doc.get_element_value("#plain") == "untouched", "an unmodelled field is untouched")
	_type_into(doc, "#plain", "typed")
	_check(doc.data.size() == 3, "typing an unmodelled field adds nothing to the data")

	# A control INSIDE a repeated row. The author writes the row's alias, which
	# means nothing at the top of the data, so the path has to be unwound
	# against the item that row actually is.
	var rows := WevaDocument.new()
	add_child(rows)
	rows.document_size = Vector2(600, 400)
	rows.html = ROWS_HTML
	rows.data = {"Quests": [
		{"Id": "a", "Title": "Find the key", "Note": "under the mat", "Done": false},
		{"Id": "b", "Title": "Open the gate", "Note": "rusted", "Done": false},
	]}
	rows.update_document()
	_check(rows.count_elements("#quests > .row") == 2, "the repeat made its rows")
	_check(rows.get_element_value("#quests > .row:nth-of-type(2) input[type=text]") == "rusted",
			"a model inside a row is filled from that row's item")

	var row_paths := []
	rows.data_changed.connect(func(path, _v): row_paths.append(path))
	_type_into(rows, "#quests > .row:nth-of-type(2) input[type=text]", "oiled")
	_check(rows.data["Quests"][1]["Note"] == "oiled", "and writes back into that item")
	_check(rows.data["Quests"][0]["Note"] == "under the mat", "leaving its neighbour alone")
	if row_paths.size() > 0:
		_check(row_paths[0] == "Quests.1.Note", "by the unwound path, not the alias")
	else:
		_check(false, "by the unwound path, not the alias")

	# A path inside a row that names no alias is global, and stays global.
	_check(rows.query_text("#quests > .row:nth-of-type(2)").contains("Open the gate"),
			"the row still reads its own fields")
	rows.queue_free()

	# A resolver owns its own data and has nowhere to put an answer, so the
	# write-back must stay out of its way rather than guess.
	var other := WevaDocument.new()
	add_child(other)
	other.document_size = Vector2(600, 400)
	other.html = HTML
	other.set_data_source(func(path): return "resolved" if path == "Player.Name" else null)
	other.update_document()
	_check(other.get_element_value("#name") == "resolved", "a resolver fills a model too")
	other.set_element_value("#name", "ignored")
	other.update_document()
	_check(true, "and writing it back does not crash")
	other.queue_free()

	_reading_back(doc)

	_gamepad_focus()
	_inline_styles()
	_screen_rects()

	doc.queue_free()
	print("godot bindings: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures > 0 else 0)
