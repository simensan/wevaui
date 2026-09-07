extends Node

var checks := 0
var failures := 0
var viewport: SubViewport

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func key(code: Key, pressed := true, unicode := 0, echo := false) -> void:
	var event := InputEventKey.new()
	event.keycode = code
	event.unicode = unicode
	event.pressed = pressed
	event.echo = echo
	viewport.push_input(event, true)

func press(code: Key) -> void:
	key(code)
	key(code, false)

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	add_child(viewport)
	var surface := Control.new()
	surface.size = Vector2(640, 480)
	viewport.add_child(surface)
	var doc := WevaDocument.new()
	doc.document_size = Vector2(400, 400)
	doc.use_engine_font = false
	doc.css = "html,body{margin:0}button,input{margin:2px}"
	doc.html = '''<button id="button" on-click="Run">Run</button>
<input id="check" type="checkbox" data-model="Settings.Music">
<form id="radios"><input id="a" type="radio" name="g"><input type="radio" name="g" disabled><input id="b" type="radio" name="g"></form>
<form><input id="other" type="radio" name="g" checked></form>
<input id="range" type="range" data-model="Settings.Volume">
<details id="details"><summary id="summary">More</summary><p>Content</p></details>
<button id="trigger" popovertarget="menu">Menu</button><div id="menu" popover>Actions</div>
<form id="login" on-submit="Submit"><input id="name"><button id="submit" on-click="Default">Submit</button></form>'''
	doc.data = {"Settings": {"Music": false, "Volume": 50}}
	surface.add_child(doc)
	var native := LineEdit.new()
	native.position = Vector2(420, 0)
	surface.add_child(native)
	await get_tree().process_frame
	doc.update_document(0)
	var clicks: Array[String] = []
	var submits: Array[String] = []
	var text_events: Array[String] = []
	var handlers: Array[String] = []
	doc.element_clicked.connect(func(id): clicks.append(id))
	doc.form_submitted.connect(func(id): submits.append(id))
	doc.text_entered.connect(func(_id, text): text_events.append(text))
	doc.handler_invoked.connect(func(handler, _id): handlers.append(handler))

	doc.set_focus("#button")
	key(KEY_ENTER)
	check(clicks == ["button"] and handlers == ["Run"], "Enter invokes the HTML click handler on key-down")
	check(viewport.is_input_handled(), "button activation is consumed before gameplay input")
	key(KEY_ENTER, true, 0, true)
	check(clicks.size() == 2, "held Enter repeats button activation")
	key(KEY_ENTER, false)
	check(clicks.size() == 2, "Enter release does not click a second time")
	clicks.clear()
	key(KEY_SPACE, true, 32)
	key(KEY_SPACE, true, 32, true)
	check(clicks.is_empty(), "Space waits for release, including repeated key-down")
	check(text_events.is_empty(), "a button's Space is not delivered as text input")
	key(KEY_SPACE, false)
	check(clicks == ["button"], "Space release clicks once")
	check(viewport.is_input_handled(), "Space release remains owned by the UI")
	key(KEY_SPACE)
	native.grab_focus()
	key(KEY_SPACE, false)
	check(clicks.size() == 1, "native focus transfer cancels a held HTML button")
	doc.set_focus("#button")
	press(KEY_SPACE)
	check(clicks.size() == 2, "the first fresh Space after native focus loss works")
	key(KEY_SPACE)
	doc.hide()
	doc.show()
	doc.set_focus("#button")
	key(KEY_SPACE, false)
	check(clicks.size() == 2, "hiding the scene cancels pending keyboard activation")

	doc.set_focus("#check")
	key(KEY_SPACE, true, 32)
	check(doc.get_element_value("#check") == "", "checkbox stays unchanged on Space down")
	key(KEY_SPACE, false)
	check(doc.get_element_value("#check") == "on", "checkbox toggles on Space release: focus=%s clicks=%s data=%s" % [doc.get_focused_id(), clicks, doc.data])
	check(doc.data.Settings.Music == true, "keyboard checkbox edits write back through data-model")
	press(KEY_ENTER)
	check(doc.get_element_value("#check") == "on", "Enter does not toggle a checkbox")

	doc.set_focus("#a")
	press(KEY_RIGHT)
	check(doc.get_focused_id() == "b" and doc.get_element_value("#b") == "on", "radio arrows skip disabled members and select the next member")
	check(doc.get_element_value("#other") == "on", "radio selection is scoped to its form")
	press(KEY_RIGHT)
	check(doc.get_focused_id() == "a" and doc.get_element_value("#b") == "", "radio arrow navigation wraps inside its group")
	press(KEY_TAB)
	check(doc.get_focused_id() == "other", "Tab leaves the radio group after one stop")

	doc.set_focus("#range")
	press(KEY_RIGHT)
	check(doc.get_element_value("#range") == "51", "slider arrows change the value")
	check(doc.data.Settings.Volume == 51, "keyboard slider edits write back through data-model")
	press(KEY_PAGEUP)
	check(doc.get_element_value("#range") == "61", "PageUp changes the slider by a page")
	press(KEY_HOME)
	check(doc.get_element_value("#range") == "0", "Home selects the slider minimum")
	press(KEY_END)
	check(doc.get_element_value("#range") == "100", "End selects the slider maximum")
	press(KEY_RIGHT)
	check(doc.get_element_value("#range") == "100", "slider arrows clamp at the endpoint")

	doc.set_focus("#summary")
	press(KEY_ENTER)
	check(doc.has_element("#details[open]"), "Enter expands details through its summary")
	press(KEY_SPACE)
	check(not doc.has_element("#details[open]"), "Space collapses details through its summary")
	doc.set_focus("#trigger")
	press(KEY_SPACE)
	check(doc.has_element("#menu[data-popover-open]"), "Space opens a popover trigger")
	press(KEY_ESCAPE)
	check(not doc.has_element("#menu[data-popover-open]"), "Escape dismisses the keyboard-opened popover")

	clicks.clear()
	handlers.clear()
	doc.set_focus("#name")
	press(KEY_ENTER)
	check(clicks == ["submit"] and submits == ["login"], "Enter in a field clicks the default button before submitting")
	check(handlers == ["Default", "Submit"], "both default-button and form handlers reach GDScript in order")
	check(doc.get_focused_id() == "name", "implicit submission keeps focus in the field")
	doc.set_element_attribute("#submit", "disabled", "")
	press(KEY_ENTER)
	check(submits.size() == 1, "a disabled default button blocks implicit submission")
	key(KEY_E, true, 233)
	check(text_events == ["é"], "text_entered preserves Unicode event text")
	check(doc.get_element_value("#name") == "é", "Unicode input still edits the focused field")

	viewport.free()
	print("godot keyboard integration: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
