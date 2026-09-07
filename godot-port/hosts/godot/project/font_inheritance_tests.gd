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

func make_doc(viewport: SubViewport, display: String, engine_font: bool, base: int, mode: String) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.document_size = Vector2(420, 420)
	doc.use_engine_font = engine_font
	doc.paused = true
	doc.interactive = false
	doc.css = "html,body{margin:0;font-size:16px}#base{font-size:16px}" + \
		"#a{font-size:2em;display:" + display + "}#b{font-size:2em}" + \
		"#sample{width:1em;height:1em;background:#09f}" + \
		"#b.inherit{font-size:inherit!important}#b.unset{font-size:unset}" + \
		"#b.initial{font-size:initial}#b.empty{font-size:var(--missing)}" + \
		"#b.relative{font-size:150%}#sample::before{content:'';display:block;" + \
		"width:0.5em;height:0.5em;background:#f80}"
	doc.html = '<div id="base" style="font-size:%dpx"><div id="a"><div id="b" class="%s"><div id="sample"></div></div></div></div>' % [base, mode]
	viewport.add_child(doc)
	doc.update_document(0)
	return doc

func _ready() -> void:
	var live_view := make_viewport()
	var fresh_view := make_viewport()
	var render := DisplayServer.get_name() != "headless"
	for engine_font in [false, true]:
		for display in ["block", "contents"]:
			var live := make_doc(live_view, display, engine_font, 16, "")
			for base: int in [16, 20, 12, 16]:
				for mode: String in ["inherit", "", "unset", "initial", "empty", "relative", ""]:
					var label := "%s/%d/%s engine=%s" % [display, base, mode, engine_font]
					check(live.set_element_style("#base", "font-size", str(base) + "px"), label + " changes ancestor")
					check(live.set_element_attribute("#b", "class", mode), label + " changes inheritance")
					live.update_document(0)
					var fresh := make_doc(fresh_view, display, engine_font, base, mode)
					var px: int = 16 if mode == "initial" else base * 3 if mode == "relative" else base * 4 if mode.is_empty() else base * 2
					var actual := live.query_bounds("#sample")
					check(actual.size.is_equal_approx(Vector2(px, px)), label + " expected size %d, got %s" % [px, actual.size])
					check(actual.is_equal_approx(fresh.query_bounds("#sample")), label + " matches fresh bounds")
					live.update_document(0)
					check(actual.is_equal_approx(live.query_bounds("#sample")), label + " survives idle update")
					if render:
						await get_tree().process_frame
						await get_tree().process_frame
						await RenderingServer.frame_post_draw
						var actual_image := live_view.get_texture().get_image()
						var control := fresh_view.get_texture().get_image()
						check(control.get_used_rect() == Rect2i(0, 0, px, px), label + " paints expected rectangle")
						check(control.get_pixel(px / 2 - 1, px / 2 - 1).is_equal_approx(Color("ff8800")) and
							control.get_pixel(px / 2, px / 2).is_equal_approx(Color("0099ff")), label + " pseudo inherits computed size")
						check(actual_image.get_data() == control.get_data(), label + " matches fresh pixels")
					fresh.free()
			live.free()
	print("godot font inheritance: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
