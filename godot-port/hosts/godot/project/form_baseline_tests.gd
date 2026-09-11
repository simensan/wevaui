extends Node

var checks := 0
var failures := 0

func check(ok: bool, description: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", description)

func viewport() -> SubViewport:
	var view := SubViewport.new()
	view.size = Vector2i(240, 160)
	view.transparent_bg = true
	view.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var container := SubViewportContainer.new()
	add_child(container)
	container.add_child(view)
	return view

func document(view: SubViewport, engine_font: bool, css: String, html: String) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.document_size = Vector2(240, 160)
	doc.use_engine_font = engine_font
	doc.paused = true
	doc.css = css
	doc.html = html
	view.add_child(doc)
	doc.update_document(0)
	return doc

func _ready() -> void:
	var live_view := viewport()
	var control_view := viewport()
	var render := DisplayServer.get_name() != "headless"
	const COMMON := "body{padding:12px;font-family:sans-serif}#row{white-space:nowrap}.shape{display:inline-block;box-sizing:border-box;width:90px;border:1px solid #555;padding:3px 4px 5px;margin:0 0 2px;font:inherit;color:black;background:white}"
	for engine_font in [false, true]:
		var live := document(live_view, engine_font, COMMON,
			'<div id="row"><input class="shape" id="field" value="1"><span id="label">Label</span></div>')
		for fs in [14, 24]:
			for height in [12, 22, 34, 60]:
				for variant in [["visible", -3], ["hidden", 0], ["auto", 7]]:
					for type in ["text", "number", "checkbox", "radio", "range", "text"]:
						var text_entry: bool = type in ["text", "number"]
						var css := COMMON + "body{font-size:%dpx}.shape{height:%dpx;margin-top:%dpx}" % [fs, height, variant[1]]
						css += ".shape{line-height:%dpx}" % (height - 10) if text_entry else ".shape{overflow:hidden;margin-bottom:0}"
						var control := document(control_view, engine_font, css,
							'<div id="row"><span class="shape" id="field">%s</span><span id="label">Label</span></div>' % ("1" if text_entry else ""))
						check(live.set_element_style("body", "font-size", "%dpx" % fs), "change parent font")
						check(live.set_element_style("#field", "height", "%dpx" % height), "change field height")
						check(live.set_element_style("#field", "margin-top", "%dpx" % variant[1]), "change top margin")
						check(live.set_element_style("#field", "overflow", variant[0]), "change overflow")
						check(live.set_element_attribute("#field", "type", type), "change field type")
						live.update_document(0)
						var label := "font%s/fs%d/h%d/%s/%s" % [engine_font, fs, height, variant, type]
						var field := live.query_bounds("#field")
						var reference_field := control.query_bounds("#field")
						var adjacent := live.query_bounds("#label")
						var reference_adjacent := control.query_bounds("#label")
						if text_entry:
							check(field.is_equal_approx(reference_field), label + " field baseline and dimensions")
							check(adjacent.is_equal_approx(reference_adjacent), label + " adjacent label baseline")
						else:
							# The empty reference has no bottom margin so its synthesized
							# baseline represents the input's border edge. Compare the
							# alignment within each row; their descent budgets differ.
							check(field.size.is_equal_approx(reference_field.size), label + " field dimensions")
							check((adjacent.position - field.position).is_equal_approx(reference_adjacent.position - reference_field.position), label + " adjacent label relative baseline: %s versus %s" % [adjacent.position - field.position, reference_adjacent.position - reference_field.position])
						if render and text_entry:
							await get_tree().process_frame
							await get_tree().process_frame
							await RenderingServer.frame_post_draw
							var a := live_view.get_texture().get_image()
							var b := control_view.get_texture().get_image()
							var other := control.query_bounds("#field")
							var area := Rect2i(int(ceil(field.position.x + 5)), int(ceil(field.position.y + 4)), 80, height - 10)
							var reference_area := Rect2i(int(ceil(other.position.x + 5)), int(ceil(other.position.y + 4)), 80, height - 10)
							check(a.get_region(area).get_data() == b.get_region(reference_area).get_data(), label + " centered text pixels within content clip")
						control.free()
		live.free()
		var short_field := document(live_view, engine_font,
			"body{padding:12px}input{display:block;box-sizing:border-box;width:90px;height:12px;margin:0;border:1px solid #555;padding:3px 4px 5px;font:24px sans-serif;background:white;color:black;outline:none}",
			'<input id="field">')
		short_field.set_focus("#field")
		short_field.set_element_selection("#field", 0, 0)
		short_field.update_document(0)
		var bounds := short_field.query_bounds("#field")
		var content_top := bounds.position + Vector2(5, 4)
		var caret := short_field.get_caret_bounds()
		check(caret.position.is_equal_approx(content_top), "short field caret begins at visible content edge")
		check(caret.size.is_equal_approx(Vector2(1, 2)), "short field caret fills the two-pixel content clip")
		if render:
			await get_tree().process_frame
			await get_tree().process_frame
			await RenderingServer.frame_post_draw
			var pixels := live_view.get_texture().get_image()
			for y in range(2):
				var color := pixels.get_pixel(int(content_top.x), int(content_top.y) + y)
				check(color.r < 0.05 and color.a > 0.95, "short field caret remains visible through the content clip: %s at %s" % [color, content_top])
		short_field.set_element_attribute("#field", "placeholder", "hint")
		short_field.update_document(0)
		check(short_field.get_caret_bounds().is_equal_approx(caret), "placeholder preserves the insertion point")
		if render:
			await get_tree().process_frame
			await get_tree().process_frame
			await RenderingServer.frame_post_draw
			var pixels := live_view.get_texture().get_image()
			for y in range(2):
				var color := pixels.get_pixel(int(content_top.x), int(content_top.y) + y)
				check(color.r < 0.05 and color.a > 0.95, "placeholder retains the full-color caret")
		short_field.set_element_value("#field", "  ")
		short_field.set_element_selection("#field", 0, 2)
		short_field.update_document(0)
		if render:
			await get_tree().process_frame
			await get_tree().process_frame
			await RenderingServer.frame_post_draw
			var pixels := live_view.get_texture().get_image()
			for y in range(2):
				var color := pixels.get_pixel(int(content_top.x) + 1, int(content_top.y) + y)
				check(color.b > color.r + 0.1, "short field selection fills the visible content height")
			var padding := pixels.get_pixel(int(content_top.x) + 1, int(content_top.y) - 1)
			check(padding.r > 0.95 and padding.g > 0.95, "short field selection stays out of the padding")
		short_field.free()
	print("godot form baselines: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
