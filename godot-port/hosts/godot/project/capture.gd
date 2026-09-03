extends Node2D

# Renders one document with the real Godot rasteriser and writes the result in
# the same format weva_render emits, so the two can be compared pixel for
# pixel. Both sides consume the identical draw list from libweva, so a
# difference here is a difference between the BACKENDS — which is the only way
# to check that the triangle-altitude render interface actually survives
# contact with a real engine.
#
#   godot --path project --rendering-driver opengl3 --scene res://capture.tscn \
#         -- --html a.html --css a.css --size 400x200 --out out.ppm [--png out.png]

var _args := {}


func _parse_args() -> void:
	var argv: PackedStringArray = OS.get_cmdline_user_args()
	var i := 0
	while i < argv.size():
		var key: String = argv[i]
		if key.begins_with("--") and i + 1 < argv.size():
			_args[key.substr(2)] = argv[i + 1]
			i += 2
		else:
			i += 1


func _read(path: String) -> String:
	if path.is_empty():
		return ""
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null:
		printerr("capture: cannot read ", path)
		return ""
	return f.get_as_text()


func _ready() -> void:
	_parse_args()
	var size_text: String = _args.get("size", "400x200")
	var parts := size_text.split("x")
	var w := int(parts[0])
	var h := int(parts[1]) if parts.size() > 1 else w

	var doc := WevaDocument.new()
	# The backend comparison only means something with the font held fixed:
	# rendering one side with the engine's face and the other with the core's
	# stub compares two different documents, not two rasterisers.
	doc.use_engine_font = not _args.has("stub-font")
	# The clock is held at zero. This scene waits two frames for the viewport
	# to become readable, and without this the animations would have run on for
	# those frames while the reference renderer stayed at the start -- the two
	# sides would then be compared at different INSTANTS, which says nothing
	# about the backends.
	doc.paused = true
	# The scene drives the pointer itself. Left interactive, the node also
	# takes LIVE input, and a mouse event on the first frame overwrote the
	# hover this scene had just set -- the captured frame was then the
	# un-hovered one while the document, correctly, said otherwise.
	doc.interactive = false
	doc.document_size = Vector2(w, h)
	# A relative url() resolves against the DOCUMENT, the way weva_render and
	# weva_dump resolve one -- and the way a browser does. Without it Godot
	# cannot open a corpus image at all, so it draws no backgrounds, no <img>
	# and no border images, while the software renderer draws all three: the
	# backend comparison then reports a difference between the two hosts that
	# is really a difference between what they were told.
	var html_path: String = _args.get("html", "")
	doc.base_path = html_path.get_base_dir()
	doc.css = _read(_args.get("css", ""))
	doc.html = _read(html_path)
	add_child(doc)
	doc.update_document()

	# The same interaction weva_render can be asked for, so the parts that
	# only exist while a user is doing something -- a cursor, a selection, a
	# hovered row, an open dropdown -- are compared between the backends too.
	# Without this the whole popup path, which draws AFTER the tree and with
	# no scissor, is verified on one side only.
	var focus: String = _args.get("focus", "")
	if not focus.is_empty():
		doc.set_focus(focus)
		var selection: String = _args.get("selection", "")
		if not selection.is_empty():
			var ends := selection.split(",")
			doc.set_element_selection(focus, int(ends[0]), int(ends[1]))
		var scroll: String = _args.get("scroll", "")
		if not scroll.is_empty():
			doc.set_element_scroll(focus, Vector2(0, float(scroll)))
	var hover: String = _args.get("hover", "")
	if not hover.is_empty():
		var box := doc.query_bounds(hover)
		doc.set_pointer(box.position + box.size * 0.5, 0)
	var open_select: String = _args.get("open", "")
	if not open_select.is_empty():
		doc.open_select(open_select)
	# The states this session added. None of them had ever been asked of the
	# Godot side by any gate, which is the same shape of gap that hid the
	# hover bug: geometry that one backend drew and the other was never asked
	# to.
	var dialog: String = _args.get("dialog", "")
	if not dialog.is_empty():
		doc.show_modal_dialog(dialog)
	var popover: String = _args.get("popover", "")
	if not popover.is_empty():
		doc.show_popover(popover)
	var press: String = _args.get("press", "")
	if not press.is_empty():
		var pbox := doc.query_bounds(press)
		var pat := pbox.position + pbox.size * 0.5
		doc.set_pointer(pat, 0)
		doc.set_pointer(pat, 1)   # down and HELD, which is what :active means
	var tooltip: String = _args.get("tooltip", "")
	if not tooltip.is_empty():
		var tbox := doc.query_bounds(tooltip)
		doc.set_pointer(tbox.position + tbox.size * 0.5, 0)
		doc.update_document()
		# A tooltip is the one piece of this that time alone brings on, so the
		# clock is advanced past its delay even though the scene is paused.
		doc.update_document(1.0)
	if not focus.is_empty() or not hover.is_empty() or not open_select.is_empty() 			or not dialog.is_empty() or not popover.is_empty() or not press.is_empty() 			or not tooltip.is_empty():
		doc.update_document()

	# The document composites over an opaque white page, matching what
	# weva_render writes; comparing against a transparent or engine-default
	# clear colour would report a difference in every untouched pixel.
	RenderingServer.set_default_clear_color(Color(1, 1, 1, 1))
	get_window().size = Vector2i(w, h)

	# One frame to submit the canvas items, a second because the viewport
	# texture is only readable after the frame that drew it has been presented.
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw

	var image := get_viewport().get_texture().get_image()
	if image.get_width() != w or image.get_height() != h:
		# A window manager may not have honoured the requested size; comparing
		# differently sized images would silently pass or fail for the wrong
		# reason, so this is fatal rather than resized away.
		printerr("capture: got %dx%d, wanted %dx%d" % [image.get_width(), image.get_height(), w, h])
		get_tree().quit(1)
		return

	var png_path: String = _args.get("png", "")
	if not png_path.is_empty():
		image.save_png(png_path)

	var out_path: String = _args.get("out", "")
	if not out_path.is_empty():
		image.convert(Image.FORMAT_RGB8)
		var f := FileAccess.open(out_path, FileAccess.WRITE)
		if f == null:
			printerr("capture: cannot write ", out_path)
			get_tree().quit(1)
			return
		f.store_string("P6\n%d %d\n255\n" % [w, h])
		f.store_buffer(image.get_data())
		f.close()

	print("capture: %d draws, %d triangles -> %s" % [doc.get_draw_count(), doc.get_triangle_count(), out_path])
	get_tree().quit(0)
