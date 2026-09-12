extends Node2D

# A headless run of demo.gd's OWN markup and script, so the demo cannot rot
# unnoticed: every handler the markup names is called, every signal it connects
# fires, and the document is asserted after each.
#
# The demo is the documentation for driving a Weva document from GDScript. A
# broken demo is a broken explanation, and nothing else in the suite reads it.

var failures := 0
var checks := 0

func _check(condition: bool, description: String) -> void:
	checks += 1
	if not condition:
		failures += 1
		printerr("FAIL  ", description)

func _ready() -> void:
	var demo = load("res://demo.gd").new()
	add_child(demo)
	# _ready built the document and wired the controller.
	var doc: WevaDocument = demo.get("_doc")
	_check(doc != null, "the demo builds a document")
	if doc == null:
		_finish()
		return
	doc.document_size = Vector2(600, 700)
	doc.update_document()

	# The bound values reached the markup.
	_check(doc.query_text(".value").contains("100"), "health is bound into the panel")
	_check(doc.query_bounds("#log").size.y > 0, "the log has a box")

	# Every handler the markup names, called the way a click would.
	demo.take_damage("")
	doc.update_document()
	_check(doc.query_text(".value").contains("90"), "take_damage moves the bound value")
	demo.heal("")
	doc.update_document()
	_check(doc.query_text(".value").contains("100"), "heal moves it back")

	# The dialog opens modally and closes, which is the flow the demo shows.
	_check(not doc.has_element_attribute("#confirm", "open"), "the dialog starts closed")
	demo.confirm_revive("")
	doc.update_document()
	_check(doc.has_element_attribute("#confirm", "open"), "confirm_revive opens it")
	_check(doc.has_element_attribute("#confirm", "data-modal"), "modally, so it dims behind")
	demo.dismiss("")
	doc.update_document()
	_check(not doc.has_element_attribute("#confirm", "open"), "dismiss closes it")

	# The log is a data-each list, and its rows carry their identity.
	demo.take_damage("")
	doc.update_document()
	var rows := doc.count_elements("#log > .entry")
	_check(rows > 0, "the log has rows")
	if rows > 0:
		var row: Dictionary = doc.get_row("#log > .entry:nth-of-type(1)")
		_check(row.has("key"), "a row knows its own key")

	demo.queue_free()
	_finish()

func _finish() -> void:
	print("godot demo: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures > 0 else 0)
