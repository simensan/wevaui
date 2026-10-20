extends Node

# Two host fixes the core cannot test, because both live in how the Godot
# host delivers events and builds canvas items.
#
# Reload during delivery: a handler that replaces the document while its own
# event is being delivered. Element handles restart with the new tree, and the
# host went on to write the old event's value back through the old handle --
# into whichever new element had inherited that index.
#
# Layer order: a `mix-blend-mode` run needs its own canvas item for its
# material, and it was a CHILD of the node's item. Godot draws a parent's
# commands before its children, so a normal element painted after the blended
# one appeared beneath it. Needs a real renderer; skipped headless.

var checks := 0
var failures := 0
var viewport: SubViewport
var doc: WevaDocument
var model := {}
var reloads := 0

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func reload(_id: String) -> void:
	reloads += 1
	# The same shape, so the old field's handle now names this one. It has a
	# value of its own and no data yet, so a write through the stale handle
	# shows up as a new "Other" entry.
	doc.html = '<input id="other" data-model="Other" value="fresh">'

func type_text(text: String) -> void:
	for c in text:
		for pressed in [true, false]:
			var event := InputEventKey.new()
			event.keycode = OS.find_keycode_from_string(c.to_upper())
			event.unicode = c.unicode_at(0)
			event.pressed = pressed
			viewport.push_input(event, true)

func _reload_during_delivery() -> void:
	doc = WevaDocument.new()
	doc.document_size = Vector2(320, 120)
	doc.css = "input{display:block;width:200px;height:30px}"
	doc.html = '<input id="field" data-model="Name" on-input="reload">'
	model = {"Name": "a"}
	doc.data = model
	doc.set_controller(self)
	viewport.add_child(doc)
	await get_tree().process_frame
	doc.set_focus("#field")
	doc.update_document(0)
	type_text("z")
	doc.update_document(0)
	await get_tree().process_frame
	check(reloads == 1, "the handler ran and reloaded (%d)" % reloads)
	check(not model.has("Other"), "nothing was written through the stale handle: %s" % str(model))
	doc.queue_free()
	await get_tree().process_frame

func _blend_layer_order() -> void:
	if DisplayServer.get_name() == "headless":
		print("godot layer order: pixel check skipped (headless)")
		return
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	viewport.transparent_bg = false
	var view := WevaDocument.new()
	view.document_size = Vector2(200, 100)
	view.css = "html,body{margin:0;background:rgb(255,255,255)} div{position:absolute;top:0;height:100px}"
	view.html = '<div style="left:0;width:120px;background:rgb(0,0,255);mix-blend-mode:multiply"></div>' + \
		'<div style="left:0;width:60px;background:rgb(255,0,0)"></div>'
	viewport.add_child(view)
	for i in 4:
		await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	var later := image.get_pixel(30, 50)
	var blended := image.get_pixel(90, 50)
	check(later.r > 0.9 and later.g < 0.1 and later.b < 0.1, "the element painted after a blended one is on top: %s" % str(later))
	check(blended.b > 0.9 and blended.r < 0.1, "the blended element still multiplies over white: %s" % str(blended))
	view.queue_free()

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(320, 120)
	add_child(viewport)
	await _reload_during_delivery()
	await _blend_layer_order()
	print("godot layer order: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
