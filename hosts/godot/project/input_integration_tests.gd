extends Node

var checks := 0
var failures := 0
var viewport: SubViewport
var surface: Control

func check(ok: bool, description: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", description)

func document(at: Vector2) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.position = at
	doc.document_size = Vector2(180, 120)
	doc.use_engine_font = false
	doc.css = "html,body{margin:0}input{width:120px;height:30px}button{display:block;width:100px;height:30px}"
	doc.html = '<input id="field"><button id="button">Go</button>'
	surface.add_child(doc)
	doc.update_document(0)
	return doc

func key(code: Key, unicode := 0, shift := false, ctrl := false, echo := false) -> void:
	var event := InputEventKey.new()
	event.keycode = code
	event.unicode = unicode
	event.pressed = true
	event.shift_pressed = shift
	event.ctrl_pressed = ctrl
	event.echo = echo
	# push_input routes to this SubViewport; action state normally comes from
	# the window's Input singleton before that dispatch.
	if code == KEY_TAB and shift: Input.action_press("ui_focus_prev")
	viewport.push_input(event, true)
	if code == KEY_TAB and shift: Input.action_release("ui_focus_prev")

func motion(at: Vector2) -> void:
	var event := InputEventMouseMotion.new()
	event.position = at
	event.global_position = at
	viewport.push_input(event, true)

func click(at: Vector2) -> void:
	motion(at)
	for pressed in [true, false]:
		var event := InputEventMouseButton.new()
		event.position = at
		event.global_position = at
		event.button_index = MOUSE_BUTTON_LEFT
		event.button_mask = MOUSE_BUTTON_MASK_LEFT if pressed else 0
		event.pressed = pressed
		viewport.push_input(event, true)

func wheel(at: Vector2, notches := 1) -> void:
	motion(at)
	for i in notches:
		var event := InputEventMouseButton.new()
		event.position = at
		event.global_position = at
		event.button_index = MOUSE_BUTTON_WHEEL_DOWN
		event.factor = 1.0
		event.pressed = true
		viewport.push_input(event, true)
		event = InputEventMouseButton.new()
		event.position = at
		event.global_position = at
		event.button_index = MOUSE_BUTTON_WHEEL_DOWN
		event.pressed = false
		viewport.push_input(event, true)

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	add_child(viewport)
	viewport.notify_mouse_entered()
	surface = Control.new()
	surface.size = Vector2(640, 480)
	viewport.add_child(surface)
	var first := document(Vector2(0, 0))
	var second := document(Vector2(220, 0))
	var native := LineEdit.new()
	native.position = Vector2(0, 160)
	native.size = Vector2(160, 40)
	surface.add_child(native)
	await get_tree().process_frame
	await get_tree().process_frame

	first.set_focus("#field")
	native.grab_focus()
	key(KEY_X, 120)
	check(native.text == "x", "native field receives typing")
	check(first.get_element_value("#field") == "", "Weva does not also edit behind a native field")
	check(first.get_focused_id() == "", "native focus clears the old HTML focus")

	first.set_element_value("#field", "")
	first.set_focus("#field")
	second.set_focus("#field")
	key(KEY_Y, 121)
	check(viewport.is_input_handled(), "accepted typing stays out of game input handlers")
	check(second.get_element_value("#field") == "y", "focused document receives typing")
	check(first.get_element_value("#field") == "", "a second document does not receive the same keystroke")
	check(native.text == "x", "Weva typing does not also edit the native field")
	check(first.get_focused_id() == "", "document focus transfers through Godot")
	key(KEY_BACKSPACE, 0, false, false, true)
	check(second.get_element_value("#field") == "", "held Backspace repeats")
	key(KEY_A, 97, false, true)
	check(second.get_element_value("#field") == "", "Ctrl+A is not inserted as text")
	key(KEY_E, 233)
	check(second.get_element_value("#field") == "é", "Unicode text crosses the native event path")
	key(KEY_Z, 122, false, true)
	check(second.get_element_value("#field") == "", "Ctrl+Z undoes typing without inserting z")
	key(KEY_Y, 121, false, true)
	check(second.get_element_value("#field") == "é", "Ctrl+Y restores the edit")
	second.set_element_value("#field", "")
	second.hide()
	key(KEY_Z, 122)
	check(second.get_element_value("#field") == "", "hidden documents do not edit")
	second.show()

	var native_clicks := [0]
	var html_clicks := [0]
	first.element_clicked.connect(func(id):
		if id == "button": html_clicks[0] += 1)
	var overlay := Button.new()
	overlay.position = first.query_bounds("#button").position
	overlay.size = first.query_bounds("#button").size
	overlay.text = "Native overlay"
	surface.add_child(overlay)
	overlay.pressed.connect(func(): native_clicks[0] += 1)
	await get_tree().process_frame
	click(overlay.position + overlay.size / 2)
	first.update_document(0)
	check(native_clicks[0] == 1, "native overlay receives its click")
	check(html_clicks[0] == 0, "clicks do not activate HTML behind a native control")
	overlay.free()
	click(first.query_bounds("#button").get_center())
	first.update_document(0)
	check(html_clicks[0] == 1, "uncovered HTML receives its click")
	check(viewport.gui_get_focus_owner() == first and first.get_focused_id() == "button", "a pointer click synchronizes native and HTML focus: %s / %s" % [viewport.gui_get_focus_owner(), first.get_focused_id()])
	first.set_focus("#field")
	key(KEY_TAB)
	check(first.get_focused_id() == "button", "Tab walks the HTML controls first")
	key(KEY_TAB)
	check(first.get_focused_id() == "" and second.get_focused_id() == "field", "Tab continues into the next document: %s / %s / %s" % [first.get_focused_id(), second.get_focused_id(), viewport.gui_get_focus_owner()])
	key(KEY_TAB, 0, true)
	check(first.get_focused_id() == "button" and second.get_focused_id() == "", "Shift+Tab enters a document at its last control")
	second.set_focus("#button")
	key(KEY_TAB)
	check(native.has_focus() and second.get_focused_id() == "", "Tab continues from HTML to a native field: %s / %s" % [second.get_focused_id(), viewport.gui_get_focus_owner()])
	key(KEY_TAB, 0, true)
	check(second.get_focused_id() == "button", "Shift+Tab returns from a native field to HTML")

	# GUI hit testing owns stacking between documents as well as native nodes.
	second.position = first.position
	second.show()
	var top_clicks := [0]
	second.element_clicked.connect(func(id):
		if id == "button": top_clicks[0] += 1)
	await get_tree().process_frame
	click(first.query_bounds("#button").get_center())
	first.update_document(0)
	second.update_document(0)
	check(top_clicks[0] == 1 and html_clicks[0] == 1, "only the top overlapping document activates")
	second.interactive = false
	click(first.query_bounds("#button").get_center())
	first.update_document(0)
	check(top_clicks[0] == 1 and html_clicks[0] == 2, "noninteractive documents let input reach the UI beneath")
	second.interactive = true
	var saved_css := second.css
	second.css += "*{pointer-events:none}"
	second.update_document(0)
	click(first.query_bounds("#button").get_center())
	first.update_document(0)
	check(top_clicks[0] == 1 and html_clicks[0] == 3, "CSS pointer-events:none lets native GUI hit testing continue beneath")
	second.css += "button{pointer-events:auto}"
	second.update_document(0)
	click(second.query_bounds("#button").get_center())
	second.update_document(0)
	check(top_clicks[0] == 2 and html_clicks[0] == 3, "an HTML descendant can restore pointer-events:auto")
	second.css = saved_css
	second.hide()

	var popup := document(Vector2(220, 0))
	popup.html = '<select id="list"><option>A</option><option>B</option></select><div id="auto" popover="auto">Auto</div><div id="manual" popover="manual">Manual</div>'
	popup.css += "select{font-size:16px}"
	popup.update_document(0)
	popup.open_select("#list")
	var select_box := popup.query_bounds("#list")
	# The stub font produces 26px rows; this point is in the second row,
	# below the select's DOM box.
	click(popup.position + Vector2(select_box.get_center().x, select_box.end.y + 39))
	check(popup.get_element_value("#list") == "B" and popup.get_open_select() == "", "a dropdown row outside the DOM content remains clickable through native GUI routing")
	popup.open_select("#list")
	click(native.position + native.size / 2)
	await get_tree().process_frame
	check(popup.get_open_select() == "", "clicking another native control dismisses an HTML dropdown")
	popup.show_popover("#auto")
	popup.show_popover("#manual")
	click(native.position + native.size / 2)
	await get_tree().process_frame
	check(not popup.has_element("#auto[data-popover-open]") and popup.has_element("#manual[data-popover-open]"), "an outside native click dismisses auto popovers and preserves manual ones")
	popup.hide_popover("#manual")
	popup.show_popover("#auto")
	popup.update_document(0)
	var inside := popup.query_bounds("#auto").get_center()
	click(popup.position + inside)
	await get_tree().process_frame
	check(popup.has_element("#auto[data-popover-open]"), "a routed click inside the popover keeps it open")
	check(popup.query_bounds("#auto").get_center() == inside, "a pointer entry does not focus and scroll an unrelated HTML field")
	var reopen := Button.new()
	reopen.position = Vector2(420, 160)
	reopen.size = Vector2(120, 40)
	surface.add_child(reopen)
	reopen.button_down.connect(func():
		popup.hide_popover("#auto")
		popup.show_popover("#auto"))
	click(reopen.position + reopen.size / 2)
	await get_tree().process_frame
	check(popup.has_element("#auto[data-popover-open]"), "a native handler can open a fresh popup without an older outside press dismissing it")
	reopen.free()
	popup.hide_popover("#auto")
	popup.set_focus("#list")
	popup.open_select("#list")
	key(KEY_TAB)
	check(popup.get_open_select() == "" and not popup.has_focus(), "Tab dismisses an open dropdown when leaving the document")
	popup.free()

	var layer := CanvasLayer.new()
	layer.transform = Transform2D(0, Vector2(1.5, 1.5), 0, Vector2(250, 230))
	viewport.add_child(layer)
	surface.remove_child(first)
	layer.add_child(first)
	first.position = Vector2(0, 0)
	await get_tree().process_frame
	var screen := first.get_global_transform_with_canvas() * first.query_bounds("#button").get_center()
	click(screen)
	first.update_document(0)
	check(html_clicks[0] == 4, "CanvasLayer transforms preserve GUI hit testing")

	var base: Node = first
	if base is Control:
		var control := base as Control
		control.size = Vector2(260, 150)
		first.update_document(0)
		check(first.document_size == Vector2(260, 150), "native Control sizing updates the HTML viewport")
		var container := HBoxContainer.new()
		container.position = Vector2(0, 350)
		container.size = Vector2(400, 100)
		surface.add_child(container)
		var item := document(Vector2())
		surface.remove_child(item)
		item.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		container.add_child(item)
		await get_tree().process_frame
		await get_tree().process_frame
		check(item.document_size == Vector2(400, 100), "a Godot Container sizes the HTML viewport")
		container.size = Vector2(500, 150)
		await get_tree().process_frame
		await get_tree().process_frame
		check(item.document_size == Vector2(500, 150) and item.query_bounds("body").size.x == 500, "container resize reflows the document")
		item.set_focus("#field")
		container.free()
		key(KEY_R, 114)
		check(viewport.gui_get_focus_owner() == null, "freeing a focused scene releases native focus")
		var anchored := WevaDocument.new()
		anchored.html = "<div>Fill the parent</div>"
		anchored.css = "html,body{margin:0}"
		surface.add_child(anchored)
		surface.size = Vector2(700, 500)
		await get_tree().process_frame
		anchored.update_document(0)
		check(anchored.document_size == Vector2(700, 500) and anchored.query_bounds("body").size.x == 700, "an unsized document follows its parent's anchors on resize")
	else:
		check(false, "the document participates in native Control layout")

	# One wheel notch is Chrome's 100 CSS px (WHEEL_DELTA 120 on Windows). Both
	# hosts scrolled 40 until 2026-09-13 -- a shared deviation from the reference;
	# the Unity feed's NativeInputFeedTests pins the same number.
	var wheeled := WevaDocument.new()
	wheeled.position = Vector2(0, 0)
	wheeled.document_size = Vector2(200, 100)
	wheeled.use_engine_font = false
	wheeled.css = "html,body{margin:0}#list{height:100px;overflow:auto;width:200px}#tall{height:1000px}"
	wheeled.html = '<div id="list"><div id="tall"></div></div>'
	surface.add_child(wheeled)
	wheeled.update_document(0)
	wheel(Vector2(100, 50))
	wheeled.update_document(0)
	check(is_equal_approx(wheeled.get_element_scroll("#list").y, 100.0), "one wheel notch scrolls Chrome's 100px, not 40")
	wheeled.free()

	viewport.free()
	print("godot input integration: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
