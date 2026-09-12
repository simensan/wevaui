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
	viewport.size = Vector2i(220, 180)
	viewport.transparent_bg = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(viewport)
	return viewport

func expected_line(raw: String, owner_fs: float, consumer_fs: float) -> float:
	match raw:
		"150%": return owner_fs * 1.5
		"2em": return owner_fs * 2.0
		"1.5", "calc(1 + 0.5)": return consumer_fs * 1.5
		"calc(1em + 50% + 2px)": return owner_fs * 1.5 + 2.0
	return -1

func make_doc(viewport: SubViewport, display: String, engine_font: bool, raw: String,
		base: int, mode: String, absolute_control: bool) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.document_size = Vector2(220, 180)
	doc.use_engine_font = engine_font
	doc.paused = true
	doc.interactive = false
	doc.css = "html,body{margin:0;font-size:16px}#p{line-height:" + raw + "}" + \
		"#c{font-size:10px;display:" + display + "}#c.own{line-height:" + raw + "}" + \
		"#c.inherit{line-height:inherit!important}#c.unset{line-height:unset}" + \
		"#g{font-size:10px;width:160px;background:#09f}" + \
		"#g::before{content:'Before';font-size:5px;display:block;width:80px;background:#f80}" + \
		"#tail{height:4px;width:160px;background:#0f0}"
	if absolute_control:
		var owner_fs := 10.0 if mode == "own" else float(base)
		doc.css += "#g{line-height:%spx}#g::before{line-height:%spx}" % [
			expected_line(raw, owner_fs, 10), expected_line(raw, owner_fs, 5)]
	doc.html = '<div id="p" style="font-size:%dpx"><div id="c" class="%s"><div id="g">Line</div></div></div><div id="tail"></div>' % [base, mode]
	viewport.add_child(doc)
	doc.update_document(0)
	return doc

func _ready() -> void:
	var live_view := make_viewport()
	var fresh_view := make_viewport()
	var render := DisplayServer.get_name() != "headless"
	for engine_font in [false, true]:
		for display in ["block", "contents"]:
			for raw in ["150%", "2em", "1.5", "calc(1 + 0.5)", "calc(1em + 50% + 2px)"]:
				var live := make_doc(live_view, display, engine_font, raw, 20, "", false)
				for base in [20, 24, 12, 20]:
					for mode in ["", "own", "inherit", "unset", ""]:
						check(live.set_element_style("#p", "font-size", "%dpx" % base), "changes ancestor font")
						check(live.set_element_attribute("#c", "class", mode), "changes inheritance")
						live.update_document(0)
						var fresh := make_doc(fresh_view, display, engine_font, raw, base, mode, true)
						var owner_fs := 10.0 if mode == "own" else float(base)
						var height := expected_line(raw, owner_fs, 10) + expected_line(raw, owner_fs, 5)
						var label := "%s/%s/%d/%s engine=%s" % [display, raw, base, mode, engine_font]
						check(is_equal_approx(live.query_bounds("#g").size.y, height), label + " computed inherited height")
						check(is_equal_approx(live.query_bounds("#tail").position.y, height), label + " following flow position")
						check(live.query_bounds("#g").is_equal_approx(fresh.query_bounds("#g")), label + " absolute control geometry")
						if render:
							await get_tree().process_frame
							await get_tree().process_frame
							await RenderingServer.frame_post_draw
							check(live_view.get_texture().get_image().get_data() == fresh_view.get_texture().get_image().get_data(),
								label + " absolute control pixels")
						fresh.free()
				live.free()
	print("godot line height: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
