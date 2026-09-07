extends Node

var checks := 0
var failures := 0
var viewport: SubViewport

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

func value(doc: WevaDocument, text: String) -> void:
	doc.set_element_value("#field", text)
	var end := text.to_utf8_buffer().size()
	doc.set_element_selection("#field", end, end)
	doc.update_document(0)

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	add_child(viewport)
	for engine_font in [false, true]:
		var doc := WevaDocument.new()
		doc.document_size = Vector2(640, 480)
		doc.use_engine_font = engine_font
		doc.css = "html,body{margin:0}input{display:block;box-sizing:border-box;width:100px;height:30px;padding:0;border:0;font-size:20px}"
		doc.html = '<input id="field"><button id="other">Other</button>'
		viewport.add_child(doc)
		await get_tree().process_frame
		doc.set_focus("#field")
		value(doc, "abcdefghijklmnopqrstuvwxyz")
		check(is_equal_approx(doc.get_caret_bounds().position.x, 99), "End scrolls the real font's caret into view")
		key(KEY_HOME)
		check(is_zero_approx(doc.get_caret_bounds().position.x), "Home reveals the start")
		key(KEY_END)
		check(is_equal_approx(doc.get_caret_bounds().position.x, 99), "End restores text scrolling")
		doc.set_pointer(Vector2(98, 15), 1)
		doc.set_pointer(Vector2(98, 15), 0)
		check(doc.get_element_selection("#field") == Vector2i(26, 26), "Clicking scrolled text selects its source offset")
		doc.set_element_attribute("#field", "style", "width:60px")
		check(is_equal_approx(doc.get_caret_bounds().position.x, 59), "Resizing keeps the caret visible")
		doc.set_element_attribute("#field", "style", "width:100px")
		value(doc, "xy")
		check(doc.get_caret_bounds().position.x > 0 and doc.get_caret_bounds().position.x < 50, "Shorter text removes excess scroll")
		value(doc, "a")
		var letter := doc.get_caret_bounds().position.x
		value(doc, "a ")
		check(doc.get_caret_bounds().position.x > letter, "Trailing space advances the caret")
		for pair in [["á", "a"], ["각", "가"], ["👨‍👩‍👧‍👦", ""], ["👩🏽‍💻", ""], ["🇸🇪", ""], ["1️⃣", ""]]:
			value(doc, pair[0])
			key(KEY_LEFT)
			check(doc.get_element_selection("#field") == Vector2i.ZERO, "Left preserves the cluster: " + pair[0])
			key(KEY_RIGHT)
			check(doc.get_element_selection("#field").x == pair[0].to_utf8_buffer().size(), "Right preserves the cluster: " + pair[0])
			key(KEY_BACKSPACE)
			check(doc.get_element_value("#field") == pair[1], "Backspace follows browser editing: " + pair[0])
			doc.undo()
			check(doc.get_element_value("#field") == pair[0], "Undo restores the complete Unicode value")
		doc.set_element_attribute("#field", "type", "password")
		value(doc, "a")
		var bullet := doc.get_caret_bounds().position.x
		for text in ["😀", "界", "á", "👨‍👩‍👧‍👦", "🇸🇪", "각", "क्‍ष"]:
			value(doc, text)
			check(is_equal_approx(doc.get_caret_bounds().position.x, bullet), "One password glyph per cluster: " + text)
		value(doc, "😀界a")
		check(is_equal_approx(doc.get_caret_bounds().position.x, 3 * bullet), "Password position counts clusters")
		doc.set_pointer(Vector2(bullet * 1.8, 15), 1)
		doc.set_pointer(Vector2(bullet * 1.8, 15), 0)
		check(doc.get_element_selection("#field") == Vector2i(7, 7), "Password clicks map back to UTF-8 bytes")
		value(doc, "😀".repeat(40))
		check(is_equal_approx(doc.get_caret_bounds().position.x, 99), "Long Unicode passwords scroll by display width")
		doc.set_composition("日本語", 3, 3)
		check(doc.get_caret_bounds().position.x <= 99, "IME preedit keeps the candidate anchor visible")
		doc.commit_composition("日本語")
		check(doc.get_element_value("#field").ends_with("日本語"), "Composition commits after a scrolled password")
		doc.free()
	viewport.free()
	print("godot text editing: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
