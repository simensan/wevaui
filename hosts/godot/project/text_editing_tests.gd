extends Node

var checks := 0
var failures := 0
var viewport: SubViewport

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func key(code: Key, control := false, shift := false) -> void:
	for pressed in [true, false]:
		var event := InputEventKey.new()
		event.keycode = code
		event.pressed = pressed
		event.ctrl_pressed = control
		event.shift_pressed = shift
		viewport.push_input(event, true)

func value(doc: WevaDocument, text: String) -> void:
	doc.set_element_value("#field", text)
	var end := text.to_utf8_buffer().size()
	doc.set_element_selection("#field", end, end)
	doc.update_document(0)

func click_at(point: Vector2) -> void:
	for pressed in [true, false]:
		var event := InputEventMouseButton.new()
		event.position = point
		event.global_position = point
		event.button_index = MOUSE_BUTTON_LEFT
		event.pressed = pressed
		viewport.push_input(event, true)

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
		value(doc, "abcdef")
		doc.set_element_selection("#field", 2, 2)
		doc.update_document(0)
		var advance := doc.get_caret_bounds().position.x
		doc.set_element_attribute("#field", "style", "transform-origin:0 0;transform:translate(120px,40px) scale(2)")
		doc.update_document(0)
		doc.set_element_selection("#field", 0, 0)
		click_at(Vector2(120 + 2 * advance, 55))
		check(doc.get_element_selection("#field") == Vector2i(2, 2), "Native pointer follows CSS transform for text selection")
		doc.set_element_attribute("#field", "style", "")
		value(doc, "abcdefghij")
		for backward in [false, true]:
			for code in [KEY_LEFT, KEY_RIGHT]:
				doc.set_element_selection("#field", 6 if backward else 2, 2 if backward else 6)
				key(code)
				var expected := 2 if code == KEY_LEFT else 6
				check(doc.get_element_selection("#field") == Vector2i(expected, expected), "Plain arrow collapses native text selection to its edge")
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
		var area := WevaDocument.new()
		area.document_size = Vector2(640, 480)
		area.use_engine_font = engine_font
		area.css = "html,body{margin:0}textarea{display:block;box-sizing:border-box;width:400px;height:200px;padding:0;border:0;font-size:20px}"
		area.html = '<textarea id="field"></textarea>'
		viewport.add_child(area)
		await get_tree().process_frame
		area.set_focus("#field")
		value(area, "aaaa")
		var paragraph_width := area.get_caret_bounds().position.x + .1
		area.set_element_attribute("#field", "style", "width:%spx;word-break:break-all" % paragraph_width)
		value(area, "aaaaaaaa\naaaa\naaaa")
		area.set_element_selection("#field", 1, 1)
		for step in [[KEY_DOWN, 10], [KEY_UP, 5], [KEY_UP, 0]]:
			key(step[0], true)
			check(area.get_element_selection("#field") == Vector2i(step[1], step[1]), "Ctrl vertical navigation skips wrapped paragraphs")
		area.set_element_selection("#field", 5, 5)
		key(KEY_DOWN, true, true)
		check(area.get_element_selection("#field") == Vector2i(5, 10), "Ctrl Shift Down preserves the selection anchor")
		value(area, "aaa\n\naaa")
		area.set_element_selection("#field", 2, 2)
		for step in [[KEY_DOWN, 4], [KEY_DOWN, 7], [KEY_UP, 4], [KEY_UP, 2]]:
			key(step[0], true)
			check(area.get_element_selection("#field") == Vector2i(step[1], step[1]), "Paragraph navigation keeps the column across empty paragraphs")
		area.set_element_attribute("#field", "style", "")
		for example in [["uppercase", "AB CD\nEF GH"], ["lowercase", "ab cd\nef gh"], ["capitalize", "Ab Cd\nEf Gh"]]:
			area.set_element_attribute("#field", "style", "text-transform:none")
			value(area, example[1])
			area.set_element_selection("#field", 1, 1)
			var expected_x := area.get_caret_bounds().position.x
			area.set_element_attribute("#field", "style", "text-transform:" + example[0])
			value(area, "ab cd\nef gh")
			area.set_element_selection("#field", 1, 1)
			check(is_equal_approx(area.get_caret_bounds().position.x, expected_x), "Styled textarea caret follows display advances")
			key(KEY_DOWN)
			check(area.get_element_selection("#field") == Vector2i(7, 7), "Styled textarea Down maps back to source")
			area.set_element_selection("#field", 8, 8)
			var styled_caret := area.get_caret_bounds()
			click_at(styled_caret.position + Vector2(.1, styled_caret.size.y / 2))
			check(area.get_element_selection("#field") == Vector2i(8, 8), "Styled textarea click maps back to source")
			check(area.get_element_value("#field") == "ab cd\nef gh", "Text styling preserves editable source")
		area.set_element_attribute("#field", "style", "")
		for whitespace in ["pre", "pre-wrap"]:
			area.set_element_attribute("#field", "style", "white-space:%s;tab-size:25px" % whitespace)
			value(area, "\t\t")
			check(is_equal_approx(area.get_caret_bounds().position.x, 50), "Fractional-space tab stops retain exact pixel advances")
			var tab_caret := area.get_caret_bounds()
			for sample in [[30, 1], [45, 2]]:
				click_at(Vector2(sample[0], tab_caret.position.y + tab_caret.size.y * .5))
				check(area.get_element_selection("#field") == Vector2i(sample[1], sample[1]), "Exact tab clicks select source boundaries")
			for zero in ["0", "0px", "0em"]:
				area.set_element_attribute("#field", "style", "white-space:%s;tab-size:%s" % [whitespace, zero])
				value(area, "a")
				var letter_x := area.get_caret_bounds().position.x
				value(area, "a\t\t")
				check(is_equal_approx(area.get_caret_bounds().position.x, letter_x), "Zero tab-size removes only display advance")
				key(KEY_LEFT)
				check(area.get_element_selection("#field") == Vector2i(2, 2), "Zero-width tabs remain editable source characters")
				value(area, "\t")
				check(is_zero_approx(area.get_caret_bounds().position.x), "A zero-width tab retains a valid caret")
		area.set_element_attribute("#field", "style", "")
		for whitespace in ["pre", "pre-wrap"]:
			area.set_element_attribute("#field", "style", "tab-size:4;white-space:" + whitespace)
			for prefix in ["a\t", "\t\t", "a\t \t", "é\t", "😀\t", "👨‍👩‍👧‍👦\t"]:
				value(area, prefix + "b")
				var end: int = prefix.to_utf8_buffer().size()
				area.set_element_selection("#field", end, end)
				var with_suffix := area.get_caret_bounds().position.x
				value(area, prefix)
				check(is_equal_approx(area.get_caret_bounds().position.x, with_suffix), "Trailing and multiple tabs preserve native caret positions")
			value(area, "a\tb")
			area.set_element_selection("#field", 1, 1)
			var left := area.get_caret_bounds()
			area.set_element_selection("#field", 2, 2)
			var right := area.get_caret_bounds().position.x
			for sample in [[0.2, 1], [0.8, 2]]:
				click_at(Vector2(lerpf(left.position.x, right, sample[0]), left.position.y + left.size.y * .5))
				check(area.get_element_selection("#field") == Vector2i(sample[1], sample[1]), "Clicks inside expanded tabs select actual source boundaries")
			value(area, "a\t\na\t")
			area.set_element_selection("#field", 2, 2)
			key(KEY_DOWN)
			check(area.get_element_selection("#field") == Vector2i(5, 5), "Down preserves the source column across tabbed lines")
			key(KEY_UP)
			check(area.get_element_selection("#field") == Vector2i(2, 2), "Up returns to the trailing tab")
		area.set_element_attribute("#field", "style", "")
		for sample in [["ab cd ef", 7], ["ab\n\ncd", 3]]:
			value(area, sample[0])
			area.set_element_selection("#field", sample[1], sample[1])
			var target := area.get_caret_bounds()
			click_at(target.position + Vector2(0.1, target.size.y * 0.5))
			check(area.get_element_selection("#field") == Vector2i(sample[1], sample[1]), "Native click finds later words and empty lines")
		for sequence in [
			["aaaaaa\na\naaaaaa", 4, [[KEY_DOWN, 8], [KEY_DOWN, 13], [KEY_UP, 8], [KEY_UP, 4]]],
			["aaaa\n\naaaa\n", 3, [[KEY_DOWN, 5], [KEY_DOWN, 9], [KEY_DOWN, 11], [KEY_UP, 9], [KEY_HOME, 6], [KEY_END, 10]]]
		]:
			value(area, sequence[0])
			area.set_element_selection("#field", sequence[1], sequence[1])
			for step in sequence[2]:
				var before_y := area.get_caret_bounds().position.y
				key(step[0])
				check(area.get_element_selection("#field") == Vector2i(step[1], step[1]), "Textarea navigation retains its column across short and empty lines")
				var after_y := area.get_caret_bounds().position.y
				if step[0] == KEY_DOWN:
					check(after_y > before_y, "Down paints the caret on the next line, including empty lines")
				elif step[0] == KEY_UP:
					check(after_y < before_y, "Up paints the caret on the previous line")
		value(area, "abc\n😀x")
		area.set_element_selection("#field", 2, 2)
		key(KEY_DOWN)
		var unicode_offset := area.get_element_selection("#field").x
		check(unicode_offset in [4, 8, 9], "Down cannot split a UTF-8 emoji")
		check(area.get_caret_bounds().size.y > 0, "Unicode destination has a valid native caret")
		value(area, "a")
		var glyph_width := area.get_caret_bounds().position.x
		area.set_element_attribute("#field", "style", "width:%fpx" % (glyph_width * 3.2))
		value(area, "aaaaaaaaa")
		for wrap in ["soft", "off", "OFF", "hard", "unknown"]:
			area.set_element_attribute("#field", "wrap", wrap)
			area.set_element_selection("#field", 1, 1)
			key(KEY_DOWN)
			var expected := 9 if wrap.to_lower() == "off" else 4
			check(area.get_element_selection("#field") == Vector2i(expected, expected), "Textarea wrap attribute controls long-word wrapping")
		area.set_element_attribute("#field", "wrap", "off")
		for wrapping in ["word-break:break-all", "overflow-wrap:break-word"]:
			area.set_element_attribute("#field", "style", "%s;white-space:pre-wrap;width:%fpx" % [wrapping, glyph_width * 3.2])
			value(area, "aaaaaaaaa")
			area.set_element_selection("#field", 1, 1)
			for step in [[KEY_DOWN, 4], [KEY_DOWN, 7], [KEY_UP, 4], [KEY_HOME, 3], [KEY_END, 6], [KEY_DOWN, 9]]:
				key(step[0])
				check(area.get_element_selection("#field") == Vector2i(step[1], step[1]), "Native textarea follows wrapped visual lines")
		area.free()
	viewport.free()
	print("godot text editing: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
