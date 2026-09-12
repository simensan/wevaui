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
	viewport.size = Vector2i(420, 260)
	viewport.transparent_bg = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(viewport)
	return viewport

func make_doc(viewport: SubViewport, display: String, engine_font: bool, state: Array) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.document_size = Vector2(420, 260)
	doc.use_engine_font = engine_font
	doc.paused = true
	doc.interactive = false
	doc.css = "html,body{margin:0;font-size:16px}#parent{width:200px;display:" + display + ";" + \
		"grid-template-columns:max-content max-content;gap:8px}" + \
		"#sample{display:inline-block;white-space:" + state[0] + ";background:#09f}" + \
		"#peer{display:inline-block;width:16px;height:16px;background:#f80}" + \
		"#reference{position:absolute;left:0;top:160px;display:inline-block;white-space:pre}"
	doc.html = '<div id="parent"><div id="sample">' + state[1] + \
		'</div><div id="peer"></div></div><div id="reference">' + state[2] + '</div>'
	viewport.add_child(doc)
	doc.update_document(0)
	return doc

func _ready() -> void:
	var live_view := make_viewport()
	var fresh_view := make_viewport()
	var render := DisplayServer.get_name() != "headless"
	var output := OS.get_environment("WEVA_INTRINSIC_OUTPUT")
	if render and not output.is_empty():
		DirAccess.make_dir_recursive_absolute(output)
	# The reference is an independent, single unwrapped line in the same font.
	# It avoids hardcoding one backend's glyph advances into the native test.
	var states := [
		["pre", "aa bbbb\ncc", "aa bbbb"],
		["normal", "aa bbbb\ncc", "aa bbbb cc"],
		["pre-wrap", "aa bbbb\ncc", "aa bbbb"],
		["pre-line", "aa bbbb  \n  cc", "aa bbbb"],
		["nowrap", "aa bbbb\ncc", "aa bbbb cc"],
		["pre", "aa\n\nbbbb cc\n", "bbbb cc"],
		["pre", "aa bbbb cc", "aa bbbb cc"],
		["pre", "aa bbbb\ncc", "aa bbbb"],
	]
	for engine_font in [false, true]:
		for display in ["block", "flex", "grid"]:
			var live := make_doc(live_view, display, engine_font, states[0])
			for step in states.size():
				var state: Array = states[step]
				var label := "%s %s step=%d engine=%s" % [display, state[0], step, engine_font]
				check(live.set_element_style("#sample", "white-space", state[0]), label + " changes whitespace")
				check(live.set_element_text("#sample", state[1]), label + " changes text")
				check(live.set_element_text("#reference", state[2]), label + " changes reference")
				live.update_document(0)
				var fresh := make_doc(fresh_view, display, engine_font, state)
				var actual := live.query_bounds("#sample")
				var expected_width := live.query_bounds("#reference").size.x
				check(absf(actual.size.x - expected_width) < 0.01,
					label + " intrinsic width: %f expected %f" % [actual.size.x, expected_width])
				check(actual.is_equal_approx(fresh.query_bounds("#sample")), label + " matches fresh geometry")
				check(live.query_bounds("#peer").is_equal_approx(fresh.query_bounds("#peer")), label + " repositions sibling")
				live.update_document(0)
				check(actual.is_equal_approx(live.query_bounds("#sample")), label + " survives idle update")
				if render:
					await get_tree().process_frame
					await get_tree().process_frame
					await RenderingServer.frame_post_draw
					var live_image := live_view.get_texture().get_image()
					var fresh_image := fresh_view.get_texture().get_image()
					check(fresh_image.get_used_rect().has_area(), label + " control paints")
					check(live_image.get_data() == fresh_image.get_data(), label + " matches fresh pixels")
					if not output.is_empty() and display == "flex" and engine_font and step == 0:
						live_image.save_png(output.path_join("incremental.png"))
						fresh_image.save_png(output.path_join("fresh.png"))
				fresh.free()
			live.free()
	print("godot intrinsic sizing: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
