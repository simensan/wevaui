extends Node

var checks := 0
var failures := 0
var viewport: SubViewport
var signals: Array[String] = []
var reset_data: Dictionary = {}

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func key(code: Key) -> void:
	for pressed in [true, false]:
		var event := InputEventKey.new()
		event.keycode = code
		event.pressed = pressed
		viewport.push_input(event, true)

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	add_child(viewport)
	var doc := WevaDocument.new()
	doc.document_size = Vector2(640, 480)
	doc.css = "html,body{margin:0}input,textarea,select{display:block;width:150px;height:34px}textarea{height:60px}"
	doc.html = '<form id="settings" on-reset="restored"><input id="name" value="Ada" data-model="Name"><textarea id="bio" data-model="Bio">Default bio</textarea><input id="sound" type="checkbox" checked data-model="Sound"><select id="quality" data-model="Quality"><option value="low">Low</option><option value="high" selected>High</option></select><button id="restore" type="reset">Restore</button><input id="input_reset" type="reset" value="Restore input"></form><input id="external" form="settings" value="outside" data-model="Outside"><input id="unowned" data-model="Unowned"><p id="label">{{ Name }} {{ Bio }} {{ Quality }}</p>'
	doc.data = {"Name":"Grace", "Bio":"Edited bio", "Sound":false, "Quality":"low", "Outside":"changed", "Unowned":"keep"}
	viewport.add_child(doc)
	await get_tree().process_frame
	doc.value_changed.connect(func(_id, _text): signals.append("input"))
	doc.value_committed.connect(func(_id, _text): signals.append("change"))
	doc.form_reset.connect(func(id): signals.append("reset:" + id); reset_data = doc.data.duplicate(true))
	doc.handler_invoked.connect(func(handler, _id): signals.append("handler:" + handler))
	check(doc.get_element_value("#name") == "Grace", "Data initializes the live value")
	check(doc.get_element_text("#bio") == "Default bio", "Textarea markup remains its reset default")
	check(doc.has_element("#name[value=Ada]"), "Attribute selectors retain input defaults")
	check(doc.has_element("#sound[checked]") and not doc.has_element("#sound:checked"), "Checkedness and the checked attribute differ")
	check(doc.get_element_value("#quality") == "low", "Select data-model sets live selectedness")
	check(doc.has_element('#quality option[value=high][selected]'), "Select model preserves selected defaults")
	doc.set_focus("#name")
	doc.paste_text("!")
	check(doc.get_element_value("#name") == "Grace!", "Typing edits the model value")
	signals.clear()
	check(doc.reset_form("#settings"), "Script reset succeeds")
	check(signals == ["handler:restored", "reset:settings"], "Reset reports its handler and signal without input/change")
	check(doc.get_element_value("#name") == "Ada", "Reset restores input default")
	check(doc.get_element_value("#bio") == "Default bio", "Reset restores textarea default")
	check(doc.get_element_value("#sound") == "on", "Reset restores checked default")
	check(doc.get_element_value("#quality") == "high", "Reset restores selected default")
	check(doc.get_element_value("#external") == "outside", "Reset reaches an external form owner")
	check(doc.get_element_value("#unowned") == "keep", "Reset respects form ownership")
	check(reset_data == {"Name":"Ada", "Bio":"Default bio", "Sound":true, "Quality":"high", "Outside":"outside", "Unowned":"keep"}, "Bound data is restored before form_reset")
	check(doc.get_element_text("#label") == "Ada Default bio high", "Bindings refresh as one reset operation")
	check(doc.get_focused_id() == "name", "Script reset preserves focus")
	check(not doc.undo(), "Reset clears the owned undo history")
	doc.set_element_attribute("#name", "value", "New default")
	check(doc.get_element_value("#name") == "New default", "Reset cleared the dirty value flag")
	doc.set_element_value("#name", "game edit")
	doc.set_element_attribute("#name", "value", "Latest default")
	check(doc.get_element_value("#name") == "game edit", "Dirty values ignore default changes")
	doc.set_focus("#restore")
	signals.clear()
	key(KEY_ENTER)
	check(doc.get_element_value("#name") == "Latest default", "Native Enter activates a reset button")
	check(signals == ["handler:restored", "reset:settings"], "Native reset emits no spurious value commit")
	doc.set_element_value("#name", "changed again")
	doc.set_focus("#input_reset")
	signals.clear()
	key(KEY_SPACE)
	check(doc.get_element_value("#name") == "Latest default", "Native Space activates input type=reset")
	check(signals == ["handler:restored", "reset:settings"], "Input reset shares the event contract")
	doc.set_focus("#bio")
	doc.set_composition("日本", 2, 2)
	doc.set_element_text("#bio", "New bio")
	check(doc.get_element_value("#bio").ends_with("日本"), "Default text changes preserve active IME")
	signals.clear()
	doc.reset_form("#settings")
	check(doc.get_element_value("#bio") == "New bio", "Reset discards preedit and restores the current default")
	check(signals == ["handler:restored", "reset:settings"], "IME reset creates no input/change signal")
	check(not doc.undo(), "Preedit does not survive in reset undo history")
	check(not doc.reset_form("#missing") and not doc.reset_form("#name"), "Reset rejects missing elements and non-forms")
	doc.free()
	viewport.free()
	print("godot form state: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
