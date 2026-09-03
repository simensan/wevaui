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

	doc.queue_free()
	print("godot bindings: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures > 0 else 0)
