extends Node

# A controller drives HTML the way its ui_* actions say: the pad and stick
# move focus by geometry, accept activates the way Space or Enter would,
# cancel is Escape and is claimed only when something closed. Events are real
# InputEventJoypadButton / InputEventJoypadMotion pushed through the viewport,
# so the project's default InputMap does the action matching.

var checks := 0
var failures := 0
var viewport: SubViewport

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func button(index: JoyButton, pressed := true) -> void:
	var event := InputEventJoypadButton.new()
	event.device = 0
	event.button_index = index
	event.pressed = pressed
	viewport.push_input(event, true)

func tap(index: JoyButton) -> void:
	button(index)
	button(index, false)

func stick(axis: JoyAxis, value: float) -> void:
	var event := InputEventJoypadMotion.new()
	event.device = 0
	event.axis = axis
	event.axis_value = value
	viewport.push_input(event, true)

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	add_child(viewport)
	var surface := Control.new()
	surface.size = Vector2(640, 480)
	viewport.add_child(surface)
	var doc := WevaDocument.new()
	doc.document_size = Vector2(400, 460)
	doc.use_engine_font = false
	doc.css = "html,body{margin:0}button,input,select{margin:4px;display:block}.grid{display:grid;grid-template-columns:120px 120px;gap:8px;width:260px}.grid button{margin:0}"
	doc.html = '''<div class="grid"><button id="b1" on-click="One">One</button><button id="b2">Two</button><button id="b3">Three</button><button id="b4">Four</button></div>
<input id="check" type="checkbox">
<input id="range" type="range" min="0" max="100" value="50" step="10">
<input id="name" type="text" value="abc">
<select id="pick"><option>First</option><option>Second</option></select>
<form id="login" on-submit="Submit"><input id="user" type="text"></form>
<button id="trigger" popovertarget="menu">Menu</button><div id="menu" popover>Actions</div>'''
	surface.add_child(doc)
	await get_tree().process_frame
	doc.update_document(0)
	var clicks: Array[String] = []
	var submits: Array[String] = []
	doc.element_clicked.connect(func(id): clicks.append(id))
	doc.form_submitted.connect(func(id): submits.append(id))

	check(not doc.has_focus(), "nothing holds focus before the controller is touched")
	button(JOY_BUTTON_DPAD_RIGHT)
	button(JOY_BUTTON_DPAD_RIGHT, false)
	check(not doc.has_focus(), "without gamepad_wake a pad press leaves an unfocused document alone")
	doc.gamepad_wake = true
	button(JOY_BUTTON_DPAD_RIGHT)
	check(doc.has_focus() and doc.get_focused_id() == "b1", "the first pad press wakes the document on its first control without moving")
	check(viewport.is_input_handled(), "and is spent doing so")
	button(JOY_BUTTON_DPAD_RIGHT, false)
	button(JOY_BUTTON_DPAD_RIGHT)
	check(doc.get_focused_id() == "b2", "D-pad right moves focus to the button beside it")
	check(viewport.is_input_handled(), "a consumed pad press does not reach gameplay")
	button(JOY_BUTTON_DPAD_RIGHT, false)
	check(not viewport.is_input_handled(), "its release passes through")
	tap(JOY_BUTTON_DPAD_DOWN)
	check(doc.get_focused_id() == "b4", "D-pad down moves to the button below, not the next in source order")
	tap(JOY_BUTTON_DPAD_LEFT)
	check(doc.get_focused_id() == "b3", "D-pad left moves back across the row")
	tap(JOY_BUTTON_DPAD_UP)
	check(doc.get_focused_id() == "b1", "D-pad up returns to the top row")
	tap(JOY_BUTTON_DPAD_UP)
	check(doc.get_focused_id() == "b1", "the top edge holds rather than wrapping")

	tap(JOY_BUTTON_A)
	check(clicks == ["b1"], "accept clicks the focused button")

	doc.set_focus("#check")
	tap(JOY_BUTTON_A)
	check(doc.get_element_value("#check") == "on", "accept toggles a checkbox on")
	tap(JOY_BUTTON_A)
	check(doc.get_element_value("#check") == "", "and off again")

	doc.set_focus("#range")
	tap(JOY_BUTTON_DPAD_RIGHT)
	check(doc.get_element_value("#range") == "60", "right on a slider steps its value")
	tap(JOY_BUTTON_DPAD_LEFT)
	check(doc.get_element_value("#range") == "50" and doc.get_focused_id() == "range", "left steps back and focus stays on the slider")
	tap(JOY_BUTTON_DPAD_DOWN)
	check(doc.get_focused_id() == "name", "down leaves the slider for the row beneath it")

	tap(JOY_BUTTON_DPAD_RIGHT)
	check(doc.get_focused_id() == "name", "right in a text field moves the caret, not the focus")
	tap(JOY_BUTTON_DPAD_DOWN)
	check(doc.get_focused_id() == "pick", "down leaves the text field")
	tap(JOY_BUTTON_DPAD_DOWN)
	check(doc.get_focused_id() == "pick" and doc.get_element_value("#pick") == "Second", "down on a closed select changes its option")
	tap(JOY_BUTTON_DPAD_UP)
	check(doc.get_element_value("#pick") == "First", "and up changes it back")

	doc.set_focus("#user")
	tap(JOY_BUTTON_A)
	check(submits == ["login"], "accept in a form field submits like Enter")

	doc.set_focus("#trigger")
	tap(JOY_BUTTON_A)
	check(doc.get_computed_style("#menu", "display") != "none", "accept opens a popover")
	button(JOY_BUTTON_B)
	check(doc.get_computed_style("#menu", "display") == "none", "cancel closes it like Escape")
	check(viewport.is_input_handled(), "a cancel that closed something is consumed")
	button(JOY_BUTTON_B, false)
	button(JOY_BUTTON_B)
	check(not viewport.is_input_handled(), "a cancel with nothing to close reaches the game")
	button(JOY_BUTTON_B, false)

	doc.set_focus("#b1")
	stick(JOY_AXIS_LEFT_X, 1.0)
	check(doc.get_focused_id() == "b2", "the left stick moves focus through ui_right")
	stick(JOY_AXIS_LEFT_X, 0.0)
	check(doc.get_focused_id() == "b2", "the stick returning to centre changes nothing")
	stick(JOY_AXIS_LEFT_Y, 1.0)
	check(doc.get_focused_id() == "b4", "and the stick's other axis moves down")
	stick(JOY_AXIS_LEFT_Y, 0.0)

	# Holding a direction repeats like a held arrow key: nothing before the
	# delay, then a step every interval until release.
	doc.set_focus("#b1")
	button(JOY_BUTTON_DPAD_DOWN)
	Input.action_press("ui_down")
	check(doc.get_focused_id() == "b3", "the press itself moves once")
	await get_tree().create_timer(0.2).timeout
	check(doc.get_focused_id() == "b3", "no repeat before the delay")
	await get_tree().create_timer(0.5).timeout
	check(doc.get_focused_id() != "b3", "holding the pad repeats after the delay")
	Input.action_release("ui_down")
	button(JOY_BUTTON_DPAD_DOWN, false)
	await get_tree().process_frame
	var settled := doc.get_focused_id()
	await get_tree().create_timer(0.3).timeout
	check(doc.get_focused_id() == settled, "release stops the repeat")

	doc.gamepad_navigation = false
	doc.set_focus("#b1")
	tap(JOY_BUTTON_DPAD_RIGHT)
	check(doc.get_focused_id() == "b1" and not viewport.is_input_handled(), "gamepad_navigation off leaves pad input to the game")

	print("godot gamepad navigation: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures > 0 else 0)
