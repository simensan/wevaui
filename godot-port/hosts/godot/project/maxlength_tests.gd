extends Node

class InputSink extends Node:
	var text_keys := 0
	func _unhandled_key_input(event: InputEvent) -> void:
		if event is InputEventKey and event.pressed and event.unicode >= 32:
			text_keys += 1

var checks := 0
var failures := 0
var viewport: SubViewport
var committed: Array[String] = []
var changed: Array[String] = []

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func key(code: Key, unicode := 0, ctrl := false) -> void:
	for pressed in [true, false]:
		var event := InputEventKey.new()
		event.keycode = code
		event.unicode = unicode if pressed else 0
		event.ctrl_pressed = ctrl
		event.pressed = pressed
		viewport.push_input(event, true)

func reset(doc: WevaDocument, value: String, limit: int) -> void:
	doc.set_element_attribute("#field", "maxlength", str(limit))
	doc.set_element_value("#field", value)
	var end := value.to_utf8_buffer().size()
	doc.set_element_selection("#field", end, end)
	doc.update_document(0)
	committed.clear()
	changed.clear()

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	add_child(viewport)
	var sink := InputSink.new()
	viewport.add_child(sink)
	for tag in ["input", "textarea"]:
		var doc := WevaDocument.new()
		doc.document_size = Vector2(640, 480)
		doc.css = "html,body{margin:0}input,textarea{display:block;width:180px;height:50px;font-size:20px}"
		doc.html = '<%s id="field" maxlength="3" data-model="Name"></%s><p id="label">{{ Name }}</p><button id="other">Other</button>' % [tag, tag]
		doc.data = {"Name": ""}
		viewport.add_child(doc)
		await get_tree().process_frame
		doc.set_focus("#field")
		doc.text_entered.connect(func(_id, text): committed.append(text))
		doc.value_changed.connect(func(_id, text): changed.append(text))
		doc.paste_text("abcdef")
		check(doc.get_element_value("#field") == "abc", "Paste obeys maxlength")
		check(committed == ["abc"] and changed == ["abc"], "Signals report the accepted insertion")
		check(doc.data.get("Name") == "abc", "Binding receives the constrained value")
		committed.clear()
		changed.clear()
		key(KEY_X, 120)
		check(doc.get_element_value("#field") == "abc", "Native typing cannot exceed the limit")
		check(committed.is_empty() and changed.is_empty(), "Rejected typing emits no edit signal")
		check(sink.text_keys == 0, "Rejected typing stays out of game input")
		check(doc.undo() and doc.get_element_value("#field").is_empty(), "Rejected typing adds no undo step")
		reset(doc, "", 2)
		check(doc.paste_text("😀界"), "Unicode paste is consumed")
		check(doc.get_element_value("#field") == "😀", "Emoji consumes two UTF-16 units")
		check(doc.get_element_selection("#field") == Vector2i(4, 4), "Selection retains its UTF-8 ABI offsets")
		reset(doc, "abc", 3)
		doc.set_element_selection("#field", 1, 2)
		doc.paste_text("XYZ")
		check(doc.get_element_value("#field") == "aXc", "Replacement uses the selection's available room")
		check(doc.get_element_selection("#field") == Vector2i(2, 2), "Caret follows accepted text")
		reset(doc, "", 1)
		doc.set_composition("日本", 2, 2)
		check(doc.get_element_value("#field") == "日本", "Preedit may exceed maxlength")
		committed.clear()
		doc.commit_composition("日本")
		check(doc.get_element_value("#field") == "日" and committed == ["日"], "Composition commit applies the limit")
		check(doc.data.get("Name") == "日", "Composition binding settles on the accepted value")
		check(doc.undo() and doc.get_element_value("#field").is_empty(), "Composition remains one undo step")
		reset(doc, "", 5)
		doc.paste_text("\nb\rc\r\nd")
		check(doc.get_element_value("#field") == ("\nb\nc\n" if tag == "textarea" else " b c "), "Pasted line endings normalize before counting")
		reset(doc, "abc", 3)
		doc.set_element_selection("#field", 1, 2)
		key(KEY_ENTER)
		check(doc.get_element_value("#field") == ("a\nc" if tag == "textarea" else "abc"), "Native Enter replaces a textarea selection")
		reset(doc, "typed", 20)
		if DisplayServer.has_feature(DisplayServer.FEATURE_CLIPBOARD):
			var clipboard_before := DisplayServer.clipboard_get()
			DisplayServer.clipboard_set("paste")
			key(KEY_V, 0, true)
			DisplayServer.clipboard_set(clipboard_before)
		else:
			doc.paste_text("paste")
		check(doc.get_element_value("#field") == "typedpaste", "Paste uses the clipboard insertion path")
		key(KEY_X, 120)
		check(doc.undo() and doc.get_element_value("#field") == "typedpaste", "Typing after paste is a separate undo step")
		check(doc.undo() and doc.get_element_value("#field") == "typed", "Paste undoes as one step")
		reset(doc, "programmatic", 1)
		check(doc.get_element_value("#field") == "programmatic", "Script values are not truncated")
		doc.free()
	viewport.free()
	print("godot maxlength native clipboard: ", DisplayServer.has_feature(DisplayServer.FEATURE_CLIPBOARD))
	print("godot maxlength: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
