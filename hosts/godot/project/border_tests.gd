extends Node

var checks := 0
var failures := 0
const SAMPLES := [
	["0,0,0", [168,168,168], [84,84,84]], ["16,16,16", [184,184,184], [100,100,100]],
	["32,32,32", [200,200,200], [116,116,116]], ["80,80,80", [164,164,164], [0,0,0]],
	["118,118,118", [202,202,202], [33,33,33]], ["128,128,128", [212,212,212], [44,44,44]],
	["235,235,235", [255,255,255], [151,151,151]], ["255,255,255", [255,255,255], [171,171,171]],
	["255,0,0", [255,0,0], [171,0,0]], ["0,0,255", [0,0,255], [0,0,171]],
	["0,128,0", [0,212,0], [0,44,0]],
]
const SHAPE_CSS := "#shape{position:absolute;left:10px;top:10px;box-sizing:border-box;width:100px;height:60px;border:8px outset currentColor}"

func check(ok: bool, description: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", description)

func viewport() -> SubViewport:
	var view := SubViewport.new()
	view.size = Vector2i(160, 160)
	view.transparent_bg = true
	view.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(view)
	view.notify_mouse_entered()
	return view

func document(view: SubViewport, engine_font: bool, css: String, html: String) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.document_size = Vector2(160, 160)
	doc.use_engine_font = engine_font
	doc.paused = true
	doc.css = css
	doc.html = html
	view.add_child(doc)
	doc.update_document(0)
	return doc

func rgba(rgb: Array, alpha: float) -> String:
	return "rgba(%d,%d,%d,%s)" % [rgb[0], rgb[1], rgb[2], str(alpha)]

func mouse(view: SubViewport, at: Vector2, pressed: bool) -> void:
	var motion := InputEventMouseMotion.new()
	motion.position = at
	motion.global_position = at
	view.push_input(motion, true)
	var event := InputEventMouseButton.new()
	event.position = at
	event.global_position = at
	event.button_index = MOUSE_BUTTON_LEFT
	event.button_mask = MOUSE_BUTTON_MASK_LEFT if pressed else 0
	event.pressed = pressed
	view.push_input(event, true)

func _ready() -> void:
	var live_view := viewport()
	var control_view := viewport()
	var render := DisplayServer.get_name() != "headless"
	for engine_font in [false, true]:
		var live := document(live_view, engine_font, SHAPE_CSS, '<div id="shape"></div>')
		for radius in [0, 20]:
			for alpha in [1.0, 0.5]:
				for sample in SAMPLES:
					for style in ["outset", "inset", "outset"]:
						var inset: bool = style == "inset"
						var leading: String = rgba(sample[2] if inset else sample[1], alpha)
						var trailing: String = rgba(sample[1] if inset else sample[2], alpha)
						var control_css := SHAPE_CSS + "#shape{border-style:solid;border-color:%s %s %s %s;border-radius:%dpx}" % [leading, trailing, trailing, leading, radius]
						var control := document(control_view, engine_font, control_css, '<div id="shape"></div>')
						check(live.set_element_style("#shape", "color", "rgba(%s,%s)" % [sample[0], str(alpha)]), "set border color")
						check(live.set_element_style("#shape", "border-style", style), "set border style")
						check(live.set_element_style("#shape", "border-radius", "%dpx" % radius), "set radius")
						live.update_document(0)
						check(live.query_bounds("#shape").is_equal_approx(Rect2(10, 10, 100, 60)), "border keeps box geometry")
						if render:
							await get_tree().process_frame
							await get_tree().process_frame
							await RenderingServer.frame_post_draw
							var picture := live_view.get_texture().get_image()
							var label := "%s/%s/r%d/a%s/font%s" % [sample[0], style, radius, alpha, engine_font]
							check(picture.get_data() == control_view.get_texture().get_image().get_data(), label + " explicit solid-color control pixels")
							for point in [Vector2i(60, 13), Vector2i(106, 40), Vector2i(60, 66), Vector2i(13, 40)]:
								var pixel := picture.get_pixelv(point)
								check(absf(pixel.a-alpha) <= 2.0/255.0, label + " single border coverage")
							if alpha == 1.0:
								var top: Array = sample[2] if inset else sample[1]
								var pixel := picture.get_pixel(60, 13)
								check(absf(pixel.r-top[0]/255.0) <= 2.0/255.0 and absf(pixel.g-top[1]/255.0) <= 2.0/255.0 and absf(pixel.b-top[2]/255.0) <= 2.0/255.0, label + " Chrome top edge color")
						control.free()
		live.free()
		var buttons := document(live_view, engine_font,
			"button{display:block;width:120px;font-size:14px;line-height:16px;background:white;margin-bottom:8px}#author{border:3px solid lime}",
			'<button id="default">Default</button><button id="author">Author</button><button id="disabled" disabled>Disabled</button>')
		check(is_equal_approx(buttons.query_bounds("#default").size.y, 22), "default button border and padding height")
		check(is_equal_approx(buttons.query_bounds("#author").size.y, 24), "author border overrides UA")
		var clicks := [0]
		buttons.element_clicked.connect(func(_id): clicks[0] += 1)
		await get_tree().process_frame
		await get_tree().process_frame
		for id in ["default", "author", "disabled"]:
			var bounds := buttons.query_bounds("#" + id)
			mouse(live_view, bounds.get_center(), true)
			buttons.update_document(0)
			if render:
				await get_tree().process_frame
				await RenderingServer.frame_post_draw
				var pixel := live_view.get_texture().get_image().get_pixel(60, int(bounds.position.y))
				if id == "default":
					check(absf(pixel.r-84.0/255.0) <= 2.0/255.0, "native pointer presses inset border")
				elif id == "author":
					check(pixel.g > 0.99 and pixel.r < 0.01, "author solid border survives native press")
			mouse(live_view, bounds.get_center(), false)
			buttons.update_document(0)
		check(clicks[0] == 2, "enabled buttons click, disabled button does not")
		buttons.free()
		var fonts := document(live_view, engine_font,
			"body{font:italic bold 30px/60px serif}button{display:block}#inherit{font:inherit}.em{display:inline-block;width:1em;height:1em}",
			'<button id="small"><span class="em" id="small-em"></span></button><button id="inherit"><span class="em" id="inherit-em"></span></button>')
		for parent_size in [30, 20, 40, 30]:
			check(fonts.set_element_style("body", "font-size", "%dpx" % parent_size), "change parent font")
			fonts.update_document(0)
			check(fonts.query_bounds("#small-em").size.is_equal_approx(Vector2.ONE * (40.0 / 3.0)), "default small-control font ignores parent font")
			check(fonts.query_bounds("#inherit-em").size.is_equal_approx(Vector2.ONE * parent_size), "authored font inherit follows parent changes")
		fonts.free()
	print("godot borders: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
