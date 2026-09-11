extends Node

var checks := 0
var failures := 0
var viewport: SubViewport
var doc: WevaDocument
var events: Array[String] = []

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func key(code: Key, shift := false, ctrl := false) -> void:
	for pressed in [true, false]:
		var event := InputEventKey.new()
		event.keycode = code
		event.pressed = pressed
		event.shift_pressed = shift
		event.ctrl_pressed = ctrl
		viewport.push_input(event, true)

func pointer(row: String, pressed: bool, shift := false, ctrl := false) -> void:
	var event := InputEventMouseButton.new()
	event.position = doc.query_bounds("#" + row).get_center()
	event.button_index = MOUSE_BUTTON_LEFT
	event.button_mask = MOUSE_BUTTON_MASK_LEFT if pressed else 0
	event.pressed = pressed
	event.shift_pressed = shift
	event.ctrl_pressed = ctrl
	viewport.push_input(event, true)

func click(row: String, shift := false, ctrl := false) -> void:
	pointer(row, true, shift, ctrl)
	pointer(row, false, shift, ctrl)

func selection(wanted: String, changed := true) -> void:
	check(doc.get_element_value("#list") == wanted, "Native selection is " + wanted)
	check(events == (["input", "change"] if changed else []), "Selection commits once, only when changed")
	check(doc.data.Choice == wanted, "Selection writes the bound model")
	events.clear()

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	viewport.handle_input_locally = true
	add_child(viewport)
	doc = WevaDocument.new()
	doc.size = Vector2(640, 480)
	doc.css = 'html,body{margin:0;background:#192333;color:#f1f5fb;font:16px sans-serif}body{padding:20px}h1{font-size:22px}select{display:block;width:280px;height:240px;padding:0;border:1px solid #8192aa;background:#26354a;color:#f1f5fb}option{height:28px;padding:2px 8px}option:disabled{color:#8192aa}#drop{height:34px;margin-top:20px}'
	doc.html = '<h1>Select controls</h1><select id="list" multiple size="6" data-model="Choice"><option id="a" value="a">Alpha</option><option id="b" value="b">Beta</option><option id="c" value="c" disabled>Charlie (disabled)</option><option id="d" value="d">Delta</option><optgroup disabled label="Off"><option id="e" value="e">Echo (disabled group)</option></optgroup><option id="g" value="g">Golf</option><option id="h" value="h">Hotel</option></select><select id="drop" size="1"><option value="low">Low</option><option value="high">High</option></select>'
	doc.data = {"Choice":""}
	viewport.add_child(doc)
	await get_tree().process_frame
	doc.value_changed.connect(func(_id, _value): events.append("input"))
	doc.value_committed.connect(func(_id, _value): events.append("change"))
	click("b")
	selection("b")
	check(doc.get_focused_id() == "list", "Option clicks focus their select")
	click("g", false, true)
	selection("b,g")
	click("d", true)
	selection("d,g")
	click("a", true)
	selection("a,b,d,g")
	click("h", true, true)
	selection("g,h")
	click("d")
	selection("d")
	click("d")
	selection("d", false)
	click("d", false, true)
	selection("")
	click("b", true)
	selection("b,d")
	click("c")
	selection("b,d", false)
	click("e")
	selection("b,d", false)
	doc.set_focus("#list")
	key(KEY_DOWN, false, true)
	selection("b,d", false)
	key(KEY_SPACE, false, true)
	selection("b")
	key(KEY_HOME)
	selection("a")
	key(KEY_END, true)
	selection("a,b,d,g,h")
	key(KEY_A, false, true)
	selection("a,b,d,g,h", false)
	key(KEY_HOME)
	selection("a")
	pointer("b", true)
	check(doc.get_element_value("#list") == "b" and events.is_empty(), "Press updates the row without committing")
	var motion := InputEventMouseMotion.new()
	motion.position = doc.query_bounds("#g").get_center()
	motion.button_mask = MOUSE_BUTTON_MASK_LEFT
	viewport.push_input(motion, true)
	check(doc.get_element_value("#list") == "b,d,g" and events.is_empty(), "Native drag extends across enabled rows")
	pointer("g", false)
	selection("b,d,g")
	check(doc.get_open_select() == "", "Listboxes keep their rows in flow")
	check(doc.query_bounds("#drop option").size == Vector2.ZERO, "size=1 hides dropdown option boxes")
	doc.set_focus("#drop")
	key(KEY_SPACE)
	check(doc.get_open_select() == "drop", "size=1 opens a dropdown through native Space")
	key(KEY_ESCAPE)
	check(doc.get_open_select() == "", "Escape dismisses the size=1 dropdown")
	doc.set_element_attribute("#drop", "size", "3")
	check(doc.query_bounds("#drop option").size.y > 0, "Changing size materializes listbox rows")
	doc.set_element_attribute("#drop", "size", "+1")
	check(doc.query_bounds("#drop option").size == Vector2.ZERO, "Parsed size changes restore dropdown rendering")
	doc.set_focus("#list")
	key(KEY_DOWN, false, true)
	var capture := OS.get_environment("WEVA_SELECT_CAPTURE")
	if not capture.is_empty():
		viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		await RenderingServer.frame_post_draw
		check(viewport.get_texture().get_image().save_png(capture) == OK, "Select preview saved")
	doc.free()
	var transformed := WevaDocument.new()
	transformed.use_engine_font = false
	transformed.document_size = Vector2(640, 480)
	transformed.css = "html,body{margin:0}select{display:block;width:160px;height:28px;font-size:14px;transform-origin:0 0;transform:translate(100px,40px) scale(1.5)}"
	transformed.html = '<select id="moved"><option value="low">Low</option><option value="med">Medium</option><option value="high">High</option></select>'
	viewport.add_child(transformed)
	await get_tree().process_frame
	for point in [Vector2(220, 60), Vector2(300, 150)]:
		for pressed in [true, false]:
			var event := InputEventMouseButton.new()
			event.position = point
			event.button_index = MOUSE_BUTTON_LEFT
			event.button_mask = MOUSE_BUTTON_MASK_LEFT if pressed else 0
			event.pressed = pressed
			viewport.push_input(event, true)
		if point.y == 60:
			check(transformed.get_open_select() == "moved", "Native click opens transformed select")
	check(transformed.get_element_value("#moved") == "high", "Native popup rows anchor to transformed control")
	check(transformed.get_open_select() == "", "Transformed popup selection dismisses it")
	transformed.free()
	viewport.free()
	print("godot select controls: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
