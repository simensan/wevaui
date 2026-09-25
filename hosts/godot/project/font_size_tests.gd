extends Node

var checks := 0
var failures := 0

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func make_viewport() -> SubViewport:
	var viewport := SubViewport.new()
	viewport.size = Vector2i(420, 420)
	viewport.transparent_bg = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(viewport)
	return viewport

func make_doc(viewport: SubViewport, size: Vector2, rule: String, inherited: bool, engine_font: bool) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.document_size = size
	doc.use_engine_font = engine_font
	doc.paused = true
	doc.css = "html,body{margin:0;font-size:16px}#sample{display:block;width:1em;height:1em;background:#09f;" + rule + "}"
	if inherited:
		doc.css += "#parent{font-size:10vw}"
	doc.html = '<div id="parent"><div id="sample"></div></div>'
	viewport.add_child(doc)
	doc.update_document(0)
	return doc

func expected_size(mode: String, size: Vector2) -> Vector2:
	var px: float
	match mode:
		"vw", "dvw": px = size.x * 0.1
		"vh": px = size.y * 0.1
		"vmin": px = minf(size.x, size.y) * 0.1
		"vmax": px = maxf(size.x, size.y) * 0.1
		"calc": px = 16.0 + size.x * 0.02
		"clamp": px = clampf(size.x * 0.1, 8.0, 40.0)
		"inherit": px = size.x * 0.15
	return Vector2(px, px)

func _ready() -> void:
	var resized_view := make_viewport()
	var fresh_view := make_viewport()
	var render := DisplayServer.get_name() != "headless"
	var output := OS.get_environment("WEVA_FONT_SIZE_OUTPUT")
	if render and not output.is_empty():
		DirAccess.make_dir_recursive_absolute(output)
	var cases := {"vw":"font-size:10vw", "vh":"font-size:10vh", "vmin":"font-size:10vmin",
		"vmax":"font-size:10vmax", "dvw":"font-size:10dvw", "calc":"font-size:calc(1em + 2vw)",
		"clamp":"font-size:clamp(8px,10vw,40px)", "inherit":"font-size:150%"}
	for engine_font in [false, true]:
		for mode: String in cases:
			var resized := make_doc(resized_view, Vector2(100, 200), cases[mode], mode == "inherit", engine_font)
			check(resized.query_bounds("#sample").size.is_equal_approx(expected_size(mode, Vector2(100, 200))),
				mode + " initial size, engine=" + str(engine_font))
			for size: Vector2 in [Vector2(200, 200), Vector2(200, 400), Vector2(400, 200), Vector2(100, 200)]:
				resized.document_size = size
				resized.update_document(0)
				var fresh := make_doc(fresh_view, size, cases[mode], mode == "inherit", engine_font)
				var actual := resized.query_bounds("#sample")
				var expected := fresh.query_bounds("#sample")
				var label := "%s at %s, engine=%s" % [mode, size, engine_font]
				check(actual.size.is_equal_approx(expected_size(mode, size)), label + " resolves viewport units")
				check(actual.is_equal_approx(expected), label + " matches fresh bounds")
				resized.update_document(0)
				check(resized.query_bounds("#sample").is_equal_approx(expected), label + " survives idle update")
				if render:
					await get_tree().process_frame
					await get_tree().process_frame
					await RenderingServer.frame_post_draw
					var actual_image := resized_view.get_texture().get_image()
					var expected_image := fresh_view.get_texture().get_image()
					check(expected_image.get_used_rect() == Rect2i(Vector2i.ZERO, Vector2i(expected_size(mode, size))),
						label + " control paints the expected rectangle")
					check(actual_image.get_data() == expected_image.get_data(), label + " matches fresh pixels")
					if not output.is_empty() and mode == "vw" and engine_font and size == Vector2(200, 200):
						actual_image.save_png(output.path_join("resized.png"))
						expected_image.save_png(output.path_join("fresh.png"))
				fresh.free()
			resized.free()
	print("godot font-size context: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
