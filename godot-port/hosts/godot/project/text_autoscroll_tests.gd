extends Node

var checks := 0
var failures := 0
var viewport: SubViewport
var doc: WevaDocument
var events: Array[String] = []
var source := ""
var case_label := ""

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ",case_label," ",message)

func motion(point: Vector2) -> void:
	var event := InputEventMouseMotion.new()
	event.position = point
	event.button_mask = MOUSE_BUTTON_MASK_LEFT
	viewport.push_input(event,true)

func button(point: Vector2, pressed: bool) -> void:
	var event := InputEventMouseButton.new()
	event.position = point
	event.button_index = MOUSE_BUTTON_LEFT
	event.button_mask = MOUSE_BUTTON_MASK_LEFT if pressed else 0
	event.pressed = pressed
	viewport.push_input(event,true)

func wait_ms(ms: int) -> void:
	var until := Time.get_ticks_msec() + ms
	while Time.get_ticks_msec() < until:
		await get_tree().process_frame

func wait_end(offset: int) -> void:
	var until := Time.get_ticks_msec() + 4000
	var frames := 0
	var frame_time := 0.0
	while Time.get_ticks_msec() < until:
		if doc.get_element_selection("#field").y == offset:
			return
		await get_tree().process_frame
		frames += 1
		frame_time += get_process_delta_time()
	check(false,"Stationary pointer reaches offset %d: selection %s, scroll %s, focus %s, bounds %s, %d frames / %.3f frame seconds / %.3f ms last update" % [offset,doc.get_element_selection("#field"),doc.get_element_scroll("#field"),doc.get_focused_id(),doc.query_bounds("#field"),frames,frame_time,doc.get_last_update_ms()])

func create_doc(kind: String, engine_font := true) -> void:
	if is_instance_valid(doc):
		doc.free()
	doc = WevaDocument.new()
	doc.size = Vector2(350,250)
	doc.use_engine_font = engine_font
	doc.paused = true
	doc.css = 'html,body{margin:0;background:#192333;color:#f1f5fb;font:16px sans-serif}input,textarea{position:absolute;left:40px;top:60px;width:160px;padding:0;border:0;background:#26354a;color:#f1f5fb;font-size:16px;line-height:24px}input{height:32px}textarea{height:96px;white-space:pre;overflow:auto}#clock{position:absolute;top:210px;width:20px;height:20px;background:#66ccaa;animation:travel 2s linear infinite}@keyframes travel{from{left:0px}to{left:200px}}'
	# Stock Godot 4.7 corrupts ScriptIterator's stack above 32 separate emoji
	# runs; the adapter shapes such runs in pieces (font_backend_tests.cpp
	# covers the limits). Ordinary coverage stays below the threshold, and the
	# stress mode passes with or without the workaround, so it is a regression
	# check only; tools/godot-text-shaping-repro retains the engine failure.
	source = ("á😀b"+"áb".repeat(12)).repeat(8)
	if OS.get_environment("WEVA_TEXT_SHAPING_STRESS") == "1":
		source = "á😀b".repeat(60)
	if kind == "textarea":
		source = ""
		for i in 20:
			source += ("\n" if i else "") + str(i) + " " + "á😀b ".repeat(12)
	doc.html = '<form id="form">' + ('<textarea id="field" data-model="Value"></textarea>' if kind == "textarea" else '<input id="field" type="%s" data-model="Value">' % kind) + '</form><button id="other">Other</button><div id="clock"></div>'
	doc.data = {"Value":source}
	viewport.add_child(doc)
	doc.set_focus("#field")
	doc.set_element_selection("#field",0,0)
	doc.update_document(0)
	doc.value_changed.connect(func(_id,_value):events.append("input"))
	doc.value_committed.connect(func(_id,_value):events.append("change"))
	events.clear()

func start_drag(area: bool) -> void:
	var b := doc.query_bounds("#field")
	button(b.position+Vector2(20,16),true)
	motion(b.position+Vector2(40,16))
	motion(Vector2(420,320 if area else b.position.y+16))
	check(events.is_empty(),"Selecting text emits no value events")

func _ready() -> void:
	var support_data := OS.get_environment("GODOT_TEXT_SUPPORT_DATA")
	if not support_data.is_empty():
		if not TextServerManager.get_primary_interface().load_support_data(support_data):
			printerr("FAIL Could not load TextServer support data")
			get_tree().quit(2)
			return
	viewport = SubViewport.new()
	viewport.size = Vector2i(640,480)
	viewport.handle_input_locally = true
	add_child(viewport)
	for engine_font in [false,true]:
		for kind in ["text","password","textarea"]:
			case_label = "%s engine font %s" % [kind,engine_font]
			create_doc(kind,engine_font)
			await get_tree().process_frame
			start_drag(kind == "textarea")
			var anchor := doc.get_element_selection("#field").x
			var clock_x := doc.query_bounds("#clock").position.x
			await wait_end(source.to_utf8_buffer().size())
			check(doc.get_element_selection("#field").x == anchor,"Native autoscroll retains the source anchor")
			check(doc.get_element_selection("#field").y == source.to_utf8_buffer().size(),"Captured drag selects the full Unicode suffix")
			check(events.is_empty() and doc.data.Value == source,"Continuous selection leaves values and bindings untouched")
			check(doc.query_bounds("#clock").position.x == clock_x,"Paused CSS leaves native selection scrolling active")
			if kind == "textarea":
				check(doc.get_element_scroll("#field").x > 0 and doc.get_element_scroll("#field").y > 0,"Textarea selection scrolls both axes")
				var capture := OS.get_environment("WEVA_TEXT_AUTOSCROLL_CAPTURE")
				if engine_font and not capture.is_empty():
					viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
					await RenderingServer.frame_post_draw
					check(viewport.get_texture().get_image().save_png(capture) == OK,"Scrolled Unicode selection preview saved")
			button(Vector2(420,320),false)
			var selected := doc.get_element_selection("#field")
			var caret := doc.get_caret_bounds()
			await wait_ms(100)
			check(doc.get_element_selection("#field") == selected and doc.get_caret_bounds() == caret,"Release stops selection and preserves its viewport")
			check(events.is_empty(),"Selection release creates no input/change")
			doc.paste_text("Z")
			var prefix := source.to_utf8_buffer().slice(0,anchor).get_string_from_utf8()
			check(doc.get_element_value("#field") == prefix+"Z" and doc.data.Value == prefix+"Z","Editing replaces the autoscrolled source range")
			doc.undo()
			check(doc.get_element_value("#field") == source,"Selection scrolling adds no undo steps")
			doc.set_element_selection("#field",source.to_utf8_buffer().size(),source.to_utf8_buffer().size())
			doc.update_document(0)
			var b := doc.query_bounds("#field")
			button(b.position+Vector2(120,16),true)
			motion(b.position+Vector2(100,16))
			motion(Vector2(0,0))
			await wait_end(0)
			check(doc.get_element_selection("#field").y == 0,"Reverse capture selects back to the start")
			button(Vector2.ZERO,false)
	case_label = "unscaled input time"
	create_doc("text")
	doc.paused = false
	await get_tree().process_frame
	Engine.time_scale = 0
	# process_frame is emitted before node processing. Let the delta already
	# computed for this frame drain before checking the new simulation scale.
	await get_tree().process_frame
	await get_tree().process_frame
	start_drag(false)
	var before := doc.get_element_selection("#field")
	var frozen := doc.query_bounds("#clock").position.x
	await wait_ms(200)
	check(doc.get_element_selection("#field").y > before.y,"Input selection advances with game time stopped")
	check(doc.query_bounds("#clock").position.x == frozen,"Game time still controls CSS animations")
	Engine.time_scale = 1
	button(Vector2(420,76),false)
	for action in ["hide","focus loss","exit tree","reset","DOM blur"]:
		case_label = action
		create_doc("textarea")
		await get_tree().process_frame
		start_drag(true)
		await wait_ms(40)
		match action:
			"hide":doc.hide()
			"focus loss":doc.notification(NOTIFICATION_WM_WINDOW_FOCUS_OUT)
			"exit tree":
				viewport.remove_child(doc)
				viewport.add_child(doc)
			"reset":check(doc.reset_form("#form"),"Reset accepts a text selection gesture")
			"DOM blur":doc.set_focus("#other")
		var selection := doc.get_element_selection("#field")
		var offset := doc.get_element_scroll("#field")
		await wait_ms(150)
		check(doc.get_element_selection("#field") == selection and doc.get_element_scroll("#field") == offset,action+" cancels continuous selection")
		check(events.is_empty(),action+" adds no text-edit event")
		button(Vector2(420,320),false)
	doc.free()
	viewport.free()
	print("godot text autoscroll: %d checks, %d failures" % [checks,failures])
	get_tree().quit(1 if failures else 0)
