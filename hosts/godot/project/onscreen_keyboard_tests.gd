extends Node

# A controller cannot type. A pad accept on a focused text field asks for
# text entry; WevaView answers with an on-screen keyboard, itself an HTML
# document the pad navigates, whose keys type into the field through the
# same text path a physical keyboard uses. The field keeps its focus and
# caret while the keyboard owns the pad, and gets the pad back when it closes.

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

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	add_child(viewport)
	var surface := Control.new()
	surface.size = Vector2(640, 480)
	viewport.add_child(surface)
	var view := WevaView.new()
	view.size = Vector2(640, 480)
	view.use_engine_font = false
	view.gamepad_wake = true
	view.css = "html,body{margin:0}input,button{display:block;margin:6px}"
	view.html = '<form id="login" on-submit="Submit"><input id="name" type="text" value=""><button id="go" type="button">Go</button></form>'
	surface.add_child(view)
	await get_tree().process_frame
	view.update_document(0)
	var opened: Array = []
	var closed: Array = []
	var requests: Array = []
	var submits: Array = []
	view.keyboard_opened.connect(func(id): opened.append(id))
	view.keyboard_closed.connect(func(id): closed.append(id))
	view.text_entry_requested.connect(func(id): requests.append(id))
	view.form_submitted.connect(func(id): submits.append(id))

	tap(JOY_BUTTON_DPAD_DOWN)
	check(view.has_focus() and view.get_focused_id() == "name", "the first pad press wakes the view on the text field")
	check(not view.is_keyboard_open(), "no keyboard before accept")
	tap(JOY_BUTTON_A)
	check(requests == ["name"] and opened == ["name"], "accept on a text field opens the on-screen keyboard")
	var keyboard := view.get_keyboard()
	check(keyboard != null and keyboard.visible and keyboard.has_focus(), "the keyboard is visible and owns the pad")
	check(view.get_focused_id() == "name" and view.retain_html_focus, "the field keeps its focus while the keyboard is open")
	check(keyboard.get_focused_id() == "k-q", "the keyboard starts on q")
	check(submits.is_empty(), "accept did not submit the form")

	tap(JOY_BUTTON_A)
	check(view.get_element_value("#name") == "q", "accept on a key types into the field")
	tap(JOY_BUTTON_DPAD_RIGHT)
	tap(JOY_BUTTON_A)
	check(view.get_element_value("#name") == "qw", "the pad moves between keys")
	keyboard.set_focus("#k-shift")
	tap(JOY_BUTTON_A)
	check(keyboard.get_focused_id() == "k-shift" and keyboard.has_element_attribute("#k-shift", "class"), "shift toggles and keeps its focus")
	keyboard.set_focus("#k-e")
	tap(JOY_BUTTON_A)
	check(view.get_element_value("#name") == "qwE", "shift capitalizes one key")
	keyboard.set_focus("#k-e")
	tap(JOY_BUTTON_A)
	check(view.get_element_value("#name") == "qwEe", "and releases after it")
	keyboard.set_focus("#k-back")
	tap(JOY_BUTTON_A)
	check(view.get_element_value("#name") == "qwE", "Back deletes")
	keyboard.set_focus("#k-space")
	tap(JOY_BUTTON_A)
	check(view.get_element_value("#name") == "qwE ", "Space types a space")
	keyboard.set_focus("#k-sym")
	tap(JOY_BUTTON_A)
	keyboard.set_focus("#k-c33")
	tap(JOY_BUTTON_A)
	check(view.get_element_value("#name") == "qwE !", "the symbol page types symbols")
	keyboard.set_focus("#k-sym")
	tap(JOY_BUTTON_A)
	check(keyboard.get_focused_id() == "k-sym" and keyboard.query_text("#k-q") == "q", "and toggles back to letters")

	keyboard.set_focus("#k-done")
	tap(JOY_BUTTON_A)
	check(not view.is_keyboard_open() and closed == ["name"], "Done closes the keyboard")
	check(view.has_focus() and view.get_focused_id() == "name" and not view.retain_html_focus, "the view gets the pad back with the field still focused")
	check(view.get_element_value("#name") == "qwE !", "the typed value stays")

	tap(JOY_BUTTON_A)
	check(view.is_keyboard_open(), "accept opens it again")
	tap(JOY_BUTTON_B)
	check(not view.is_keyboard_open(), "cancel closes it")

	tap(JOY_BUTTON_A)
	keyboard.set_focus("#k-enter")
	tap(JOY_BUTTON_A)
	check(submits == ["login"] and not view.is_keyboard_open(), "Enter submits the form like the key and closes the keyboard")

	view.on_screen_keyboard = false
	tap(JOY_BUTTON_A)
	check(requests.size() == 4 and not view.is_keyboard_open(), "with the keyboard off the request still fires for a custom handler")
	view.gamepad_text_entry = false
	tap(JOY_BUTTON_A)
	check(submits == ["login", "login"], "with text entry off accept is Enter")

	print("godot on-screen keyboard: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures > 0 else 0)
