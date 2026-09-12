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

func motion(point: Vector2) -> void:
	var event := InputEventMouseMotion.new()
	event.position = point
	event.button_mask = MOUSE_BUTTON_MASK_LEFT
	viewport.push_input(event, true)

func button(point: Vector2, pressed: bool) -> void:
	var event := InputEventMouseButton.new()
	event.position = point
	event.button_index = MOUSE_BUTTON_LEFT
	event.button_mask = MOUSE_BUTTON_MASK_LEFT if pressed else 0
	event.pressed = pressed
	viewport.push_input(event, true)

func wait_ms(ms: int) -> void:
	var until := Time.get_ticks_msec() + ms
	while Time.get_ticks_msec() < until:
		await get_tree().process_frame

func wait_scroll(bottom: bool) -> void:
	var until := Time.get_ticks_msec() + 2500
	while Time.get_ticks_msec() < until:
		var y := doc.get_element_scroll("#list").y
		var target := doc.get_element_scroll_max("#list").y if bottom else 0.0
		if is_equal_approx(y, target):
			return
		await get_tree().process_frame
	check(false, "Autoscroll reaches the requested boundary")

func create_doc() -> void:
	if is_instance_valid(doc):
		doc.free()
	doc = WevaDocument.new()
	doc.size = Vector2(350, 250)
	doc.paused = true
	doc.css = 'html,body{margin:0;font:16px sans-serif;background:#192333;color:#f1f5fb}select{position:absolute;left:20px;top:50px;width:280px;height:110px;padding:0;border:0;background:#26354a;color:#f1f5fb}option{height:24px;padding:0}option:disabled{color:#8192aa}#clock{position:absolute;top:210px;width:20px;height:20px;background:#66ccaa;animation:travel 2s linear infinite}@keyframes travel{from{left:0px}to{left:200px}}'
	var html := '<form id="form"><select id="list" multiple size="4" data-model="Choice">'
	for i in range(20):
		html += '<option id="o%d" value="%d" %s>Row %d</option>' % [i, i, 'disabled' if i in [5,19] else '', i]
	doc.html = html + '</select></form><div id="clock"></div>'
	doc.data = {"Choice":""}
	viewport.add_child(doc)
	doc.value_changed.connect(func(_id, _value): events.append("input"))
	doc.value_committed.connect(func(_id, _value): events.append("change"))
	events.clear()

func start_drag() -> void:
	button(doc.query_bounds("#o1").get_center(), true)
	motion(doc.query_bounds("#o2").get_center())
	motion(Vector2(160, 320)) # Beyond both the select and its Godot Control.
	check(doc.get_element_value("#list") == "1,2", "Captured drag selects the crossed rows")
	check(events.is_empty(), "Held drag defers input/change")

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	viewport.handle_input_locally = true
	add_child(viewport)
	create_doc()
	await get_tree().process_frame
	start_drag()
	var clock_x := doc.query_bounds("#clock").position.x
	await wait_scroll(true)
	check(doc.get_element_scroll("#list").y > 300, "Native capture scrolls while the pointer stays outside the Control")
	check(doc.get_element_value("#list") == "1,2" and events.is_empty(), "Stationary scrolling preserves selectedness and pending commit")
	check(doc.data.Choice == "", "The model waits for the completed gesture")
	check(doc.query_bounds("#clock").position.x == clock_x, "Paused CSS time does not stop input scrolling")
	motion(doc.query_bounds("#o18").get_center())
	var expected := PackedStringArray()
	for i in range(1,19):
		if i != 5:
			expected.append(str(i))
	check(doc.get_element_value("#list") == ",".join(expected), "Reentering a visible row extends the range across enabled options")
	button(Vector2(160, 320), false)
	check(events == ["input", "change"], "Outside release commits exactly once")
	check(doc.data.Choice == ",".join(expected), "Committed range reaches the model")
	var capture := OS.get_environment("WEVA_SELECT_AUTOSCROLL_CAPTURE")
	if not capture.is_empty():
		viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		await RenderingServer.frame_post_draw
		check(viewport.get_texture().get_image().save_png(capture) == OK, "Autoscrolled selection preview saved")
	events.clear()
	var settled := doc.get_element_scroll("#list").y
	await wait_ms(150)
	check(doc.get_element_scroll("#list").y == settled and events.is_empty(), "Release stops timed scrolling")
	button(doc.query_bounds("#o18").get_center(), true)
	motion(doc.query_bounds("#o17").get_center())
	motion(Vector2(160, 20))
	await wait_scroll(false)
	check(doc.get_element_scroll("#list").y == 0, "Captured drag can scroll upward")
	button(Vector2(160,20), false)
	doc.paused = false
	await wait_ms(120)
	check(doc.query_bounds("#clock").position.x != clock_x, "CSS animation resumes normally")

	for action in ["hide", "focus loss", "exit tree", "noninteractive", "reset"]:
		create_doc()
		await get_tree().process_frame
		start_drag()
		await wait_ms(60)
		check(doc.get_element_scroll("#list").y > 0, action + " starts with an active timed gesture")
		match action:
			"hide": doc.hide()
			"focus loss": doc.notification(NOTIFICATION_WM_WINDOW_FOCUS_OUT)
			"exit tree":
				viewport.remove_child(doc)
				viewport.add_child(doc)
			"noninteractive": doc.interactive = false
			"reset": check(doc.reset_form("#form"), "Form reset accepts the captured select")
		settled = doc.get_element_scroll("#list").y
		await wait_ms(150)
		check(doc.get_element_scroll("#list").y == settled, action + " cancels autoscroll")
		button(Vector2(160,320), false)
	doc.free()
	viewport.free()
	print("godot select autoscroll: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
