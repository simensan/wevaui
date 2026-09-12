extends Node2D

# Does hover survive the way the GALLERY shows a sample?
#
# The gallery does not draw a document at 1:1 into the corner of the window. It
# puts it in a stage node, scales it to fit and offsets it by a scroll pan --
# so a mouse position has to come back through that whole transform before it
# means anything to the document. Nothing tested that: the interactive gate
# drives the engine through set_pointer, in document coordinates, which is the
# one path that cannot get the transform wrong.

var failures := 0
var checks := 0

func _check(condition: bool, description: String) -> void:
	checks += 1
	if not condition:
		failures += 1
		printerr("FAIL  ", description)

const CSS := """
.card { width: 200px; height: 80px; background: rgb(10, 10, 10); color: rgb(1, 2, 3); cursor: pointer; }
.card:hover { background: rgb(200, 30, 40); }
.card:hover .title { color: rgb(9, 9, 9); }
.card:hover + .next { color: rgb(7, 7, 7); }
.far { width: 200px; height: 80px; }
"""

const HTML := """
<div class="card" id="card"><span class="title" id="title">hover me</span></div>
<div class="next" id="next">sibling</div>
<div class="far" id="far">far away</div>
"""

func _hovering(doc: WevaDocument) -> bool:
	# The LONGHAND. `background` is a shorthand and computes to nothing, so a
	# probe reading it never sees a colour and reports every state as unhovered.
	return doc.get_computed_style("#card", "background-color").contains("200")

# A real InputEventMouseMotion, at a GLOBAL screen position, through the same
# GUI input path the window delivers to. The scene supplies a sized
# SubViewport because the headless display's root window is only 64x64.
func _move_to(doc: WevaDocument, global_point: Vector2) -> void:
	var ev := InputEventMouseMotion.new()
	ev.global_position = global_point
	ev.position = global_point
	# Through the viewport, which is how a window delivers one -- the node's
	# own _input is not callable from script on a GDExtension class.
	get_viewport().push_input(ev, true)
	doc.update_document()

func _ready() -> void:
	get_viewport().notify_mouse_entered()
	var stage := Node2D.new()
	add_child(stage)

	var doc := WevaDocument.new()
	doc.document_size = Vector2(600, 400)
	doc.css = CSS
	doc.html = HTML
	stage.add_child(doc)
	doc.update_document()
	await get_tree().process_frame
	await get_tree().process_frame

	# 1:1, no transform. If this fails, hover is broken outright.
	_check(not _hovering(doc), "nothing is hovered to start with")
	var stats: Dictionary = doc.get_stats()
	_check(stats["boxes"] > 0 and stats["elements"] > 0 and stats["updates"] >= 1,
			"the engine counters describe the document")
	_check(stats["update_ms"] >= 0.0 and stats["draws"] >= 1, "and its last update")
	var boxes: Array = doc.get_box_tree()
	_check(boxes.size() >= 6 and boxes[0]["parent"] == -1, "the box tree lists the root first")
	var saw_text := false
	for b in boxes:
		if b["kind"] == "text" and b.get("text", "") == "hover": saw_text = true
	_check(saw_text, "and the title's first word as a text run (words are runs of their own)")
	# A blended box draws through its own item; the draw list says which.
	var blended := WevaDocument.new()
	blended.position = Vector2(2000, 2000)   # off to the side: it must not sit under the pointer tests below
	blended.document_size = Vector2(200, 100)
	blended.css = "#m { width: 50px; height: 20px; mix-blend-mode: multiply; background: rgb(255, 0, 0) }"
	blended.html = "<div id=m></div>"
	stage.add_child(blended)
	blended.update_document()
	await get_tree().process_frame
	_check(blended.get_stats()["draws"] >= 1, "a mix-blend-mode box still draws")
	_check(blended.get_html_diagnostics().is_empty(), "clean markup has no parse diagnostics")
	blended.html = "<div><span>x</div>"
	var diag: PackedStringArray = blended.get_html_diagnostics()
	_check(diag.size() == 1 and diag[0].begins_with("1:") and diag[0].contains("span"),
			"a mismatched end tag is reported with its position")
	# Back to the blended box (the diagnostics check replaced the markup), then
	# restyle it: the change list names it, the structure version stands.
	blended.html = "<div id=m></div>"
	blended.update_document()
	var version: int = blended.get_structure_version()
	blended.css = "#m { width: 50px; height: 20px; background: rgb(0, 0, 255) }"
	blended.update_document()
	_check(blended.get_changed_elements().size() >= 1, "a restyle names the elements it touched")
	_check(blended.get_structure_version() == version, "without moving the structure version")
	stage.remove_child(blended)
	blended.free()

	# Isolate the two halves before asserting on them together: does the
	# ENGINE hover, and does an event delivered by the window reach it?
	doc.set_pointer(Vector2(100, 40), 0)
	doc.update_document()
	_check(_hovering(doc), "the engine hovers when told in document coordinates")
	doc.clear_pointer()
	doc.update_document()

	_move_to(doc, Vector2(100, 40))
	_check(_hovering(doc), "hover lands at 1:1")
	_check(doc.get_cursor() == "pointer", "the CSS cursor is the card's pointer under it")
	_check(doc.get_cursor_shape(Vector2(100, 40)) == Control.CURSOR_POINTING_HAND,
			"and the node answers Godot's shape query with the pointing hand")
	_check(doc.get_computed_style("#title", "color").contains("9"),
			"and a descendant rule follows it")
	_check(doc.get_computed_style("#next", "color").contains("7"),
			"and a sibling rule follows it")

	_move_to(doc, Vector2(100, 300))
	_check(not _hovering(doc), "and leaves when the pointer does")
	_check(doc.get_cursor() == "default", "off the content it is the arrow")
	_check(doc.get_cursor_shape(Vector2(100, 300)) == Control.CURSOR_ARROW, "and the shape is the arrow")
	_move_to(doc, Vector2(20, 106))
	_check(doc.get_cursor() == "text", "over the plain text it is the I-beam")
	_check(doc.get_cursor_shape(Vector2(20, 106)) == Control.CURSOR_IBEAM, "and the shape is the I-beam")
	_move_to(doc, Vector2(100, 300))
	doc.follow_css_cursor = false
	_check(doc.get_cursor_shape(Vector2(100, 40)) == Control.CURSOR_ARROW, "off: the arrow everywhere")
	doc.follow_css_cursor = true

	# Now the way the gallery actually shows it: scaled to fit, and panned.
	doc.scale = Vector2(0.5, 0.5)
	doc.position = Vector2(120, 40)
	doc.update_document()

	# The card's middle is (100, 40) in DOCUMENT space, which at half scale
	# offset by (120, 40) is (170, 60) on screen.
	_move_to(doc, Vector2(170, 60))
	_check(_hovering(doc), "hover lands through a scale and an offset")

	# And the un-transformed position must now MISS, or the transform is being
	# ignored rather than applied.
	_move_to(doc, Vector2(100, 300))
	_check(not _hovering(doc), "and leaves again")

	# A pan, which is what scrolling the gallery does.
	doc.position = Vector2(120, -10)
	doc.update_document()
	_move_to(doc, Vector2(170, 10))
	_check(_hovering(doc), "hover survives a pan")

	# The stage itself carrying a transform, rather than the document.
	doc.scale = Vector2.ONE
	doc.position = Vector2.ZERO
	stage.scale = Vector2(2.0, 2.0)
	stage.position = Vector2(10, 10)
	doc.update_document()
	_move_to(doc, Vector2(210, 90))
	_check(_hovering(doc), "and a transform on an ANCESTOR node")

	# ABI minor 36: the safe-area insets a page pads with through env().
	var notched := WevaDocument.new()
	notched.document_size = Vector2(300, 200)
	notched.css = "html, body { margin: 0 } #pad { padding-top: env(safe-area-inset-top); height: 10px }"
	notched.html = "<div id=pad></div>"
	notched.position = Vector2(2000, 2000)
	add_child(notched)
	notched.update_document()
	_check(notched.query_bounds("#pad").size.y == 10, "no insets until the host sets them")
	notched.set_safe_area_insets(44, 0, 0, 0)
	notched.update_document()
	_check(notched.query_bounds("#pad").size.y == 54, "and env(safe-area-inset-top) pads by what it set")
	_check(notched.get_safe_area_insets() == Vector4(44, 0, 0, 0), "which the node reports back")
	notched.follow_display_safe_area = true
	notched.update_document()
	_check(notched.follow_display_safe_area, "following the display is a property that reads back")
	notched.queue_free()

	doc.queue_free()
	print("godot hover: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures > 0 else 0)
