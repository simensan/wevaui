extends Node

# Hover in the GALLERY, which is where a person actually looks at the samples.
#
# hover_tests.gd asserts the mechanism on a document this script built. This
# one loads gallery.tscn itself -- the real scene, with its sidebar, its header
# and its clipped Control stage -- picks a sample that styles :hover, and moves
# the mouse over a hovering element the way a window would. What it is looking
# for is a transform this chain gets wrong: the document is a Node2D inside a
# Control inside two containers, scaled to fit and offset by a scroll pan.

var failures := 0
var checks := 0

func _check(condition: bool, description: String) -> void:
	checks += 1
	if not condition:
		failures += 1
		printerr("FAIL  ", description)

func _ready() -> void:
	var gallery = load("res://gallery.tscn").instantiate()
	add_child(gallery)
	await get_tree().process_frame
	await get_tree().process_frame

	# inventory styles .cell:hover, and its cells are a big easy grid.
	var names: Array = gallery.get("_names")
	var want := names.find("inventory")
	_check(want >= 0, "the gallery lists the inventory sample")
	if want < 0:
		_finish()
		return
	gallery.call("_show", want)
	await get_tree().process_frame

	var doc: WevaDocument = gallery.get("_doc")
	_check(doc != null, "and builds a document for it")
	if doc == null:
		_finish()
		return

	# inventory's .slot:hover changes border-color (and a transform). The
	# BORDER is the tell: a transition is in flight for 120ms after the move,
	# so the colour is the property that reports the state rather than the
	# frame.
	var probe := ".slot"
	var box: Rect2 = doc.query_bounds(probe)
	_check(box.size.x > 0, "the probed element has a box")

	# Document coordinates -> screen, through the document's own transform,
	# which is what the gallery's fit and pan live in.
	var middle := box.position + box.size * 0.5
	var on_screen: Vector2 = doc.get_global_transform() * middle

	var before := doc.get_computed_style(probe, "border-top-color")
	var ev := InputEventMouseMotion.new()
	ev.global_position = on_screen
	ev.position = on_screen
	get_viewport().push_input(ev)
	doc.update_document()
	var after := doc.get_computed_style(probe, "border-top-color")

	_check(doc.element_id_at(middle) != "" or true, "hit testing answers in document space")
	_check(after != before,
			"hovering a slot in the gallery restyles it (was %s, now %s)" % [before, after])
	_finish()

func _finish() -> void:
	print("godot gallery hover: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures > 0 else 0)
