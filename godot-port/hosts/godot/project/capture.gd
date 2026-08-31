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
	doc.document_size = Vector2(w, h)
	doc.css = _read(_args.get("css", ""))
	doc.html = _read(_args.get("html", ""))
	add_child(doc)
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
