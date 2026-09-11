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

func key(code: Key, character := 0, shift := false, ctrl := false, alt := false) -> void:
	for pressed in [true, false]:
		var event := InputEventKey.new()
		event.keycode = code
		event.unicode = character
		event.pressed = pressed
		event.shift_pressed = shift
		event.ctrl_pressed = ctrl
		event.alt_pressed = alt
		viewport.push_input(event, true)

func type_text(text: String) -> void:
	for i in text.length():
		key(KEY_NONE, text.unicode_at(i))

func refocus() -> void:
	doc.set_focus("#other")
	doc.set_focus("#s")
	events.clear()

func value(expected: String, expected_events: Array) -> void:
	check(doc.get_element_value("#s") == expected, "Typeahead chooses %s (got %s)" % [expected, doc.get_element_value("#s")])
	check(events == expected_events, "Native typeahead event timing: %s, expected %s" % [events, expected_events])
	check(doc.data.Choice == expected, "Typeahead writes data-model through native events")
	events.clear()

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	viewport.handle_input_locally = true
	add_child(viewport)
	doc = WevaDocument.new()
	doc.size = Vector2(640, 480)
	doc.css = 'html,body{margin:0;background:#192333;color:#f1f5fb;font:16px sans-serif}body{padding:20px}h1{font-size:22px}select{display:block;width:320px;height:290px;padding:4px;border:1px solid #8192aa;background:#26354a;color:#f1f5fb}option{height:28px;padding:2px 8px}optgroup option{padding-left:24px}option:disabled{color:#8192aa}button{margin-top:12px}'
	doc.html = '<h1>Type to choose</h1><select id="s" multiple size="7" data-model="Choice"><option value="a">Alpha</option><option value="b">Beta</option><option value="bl">Blue bird</option><option value="bu">Bumblebee</option><optgroup id="group" label="Cities"><option id="city" value="paris" label="Évry">Internal city name</option><option value="oslo">Øresund</option></optgroup><optgroup label="Unavailable" disabled><option value="bad">Berlin</option></optgroup></select><button id="other" type="button">Other</button>'
	doc.data = {"Choice":"a"}
	doc.css += '#drop{position:absolute;left:365px;top:80px;width:245px;height:34px}#drop optgroup{font-style:italic;color:#b9cff8}'
	doc.html += '<select id="drop"><optgroup id="dropgroup" label="Destinations"><option value="aland">Åland</option><option value="malmo">Malmö with a label longer than its popup panel</option></optgroup><optgroup label="Unavailable" disabled><option value="bad">Madrid</option></optgroup></select>'
	viewport.add_child(doc)
	await get_tree().process_frame
	doc.value_changed.connect(func(_id, _value): events.append("input"))
	doc.value_committed.connect(func(_id, _value): events.append("change"))
	doc.set_focus("#s")
	type_text("b")
	value("b", ["change"])
	type_text("b")
	value("bl", ["change"])
	type_text("l")
	value("bl", [])
	refocus()
	type_text("blue")
	value("bl", ["change", "change", "change", "change"])
	key(KEY_SPACE, 32)
	value("bl", ["change"])
	type_text("bi")
	value("bl", ["change", "change"])
	refocus()
	type_text("e")
	value("paris", ["change"])
	check(doc.get_element_text("#city") == "Internal city name", "Visible labels preserve DOM text and submitted values")
	check(doc.query_bounds("#group").size.y > doc.query_bounds("#city").size.y * 2, "Optgroup heading occupies its own row")
	refocus()
	type_text("o")
	value("oslo", ["change"])
	refocus()
	key(KEY_B, 98, false, true)
	key(KEY_B, 98, false, false, true)
	value("oslo", [])
	key(KEY_B, 66, true)
	value("b", ["change"])
	refocus()
	doc.paused = true
	type_text("x")
	value("b", [])
	# SceneTree timers advance by frame delta, which may include work before
	# their creation on the first rendered frame. Typeahead uses wall time.
	var deadline := Time.get_ticks_msec() + 1100
	while Time.get_ticks_msec() < deadline:
		await get_tree().create_timer(0.05).timeout
	type_text("e")
	value("paris", ["change"])
	doc.paused = false
	doc.set_element_attribute("#city", "label", "Åland")
	doc.set_element_attribute("#group", "label", "Updated cities")
	refocus()
	type_text("a")
	value("a", ["change"])
	type_text("a")
	value("paris", ["change"])
	doc.set_focus("#drop")
	check(doc.open_select("#drop"), "Grouped dropdown opens")
	events.clear()
	type_text("m")
	check(doc.get_element_value("#drop") == "aland" and events.is_empty(), "Open popup typeahead defers selection")
	# The first display row is a heading. It owns the click without selecting
	# the first option or dismissing the popup.
	var drop_bounds := doc.query_bounds("#drop")
	for pressed in [true, false]:
		var click := InputEventMouseButton.new()
		click.position = drop_bounds.position + Vector2(12, drop_bounds.size.y + 3)
		click.button_index = MOUSE_BUTTON_LEFT
		click.button_mask = MOUSE_BUTTON_MASK_LEFT if pressed else 0
		click.pressed = pressed
		viewport.push_input(click, true)
	check(doc.get_open_select() == "drop" and events.is_empty(), "Popup group heading remains unselectable")
	key(KEY_ENTER)
	check(doc.get_element_value("#drop") == "malmo" and events == ["input", "change"], "Popup commit maps its heading rows to the highlighted option")
	doc.set_element_attribute("#dropgroup", "label", "Updated destinations")
	doc.open_select("#drop")
	var capture := OS.get_environment("WEVA_TYPEAHEAD_CAPTURE")
	if not capture.is_empty():
		viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		await RenderingServer.frame_post_draw
		check(viewport.get_texture().get_image().save_png(capture) == OK, "Typeahead preview saved")
	doc.free()
	viewport.free()
	print("godot typeahead: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
