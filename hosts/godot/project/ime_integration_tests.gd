extends Node

var checks := 0
var failures := 0

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func _ready() -> void:
	var container := SubViewportContainer.new()
	container.position = Vector2(9, 13)
	add_child(container)
	var viewport := SubViewport.new()
	viewport.size = Vector2i(640, 480)
	container.add_child(viewport)
	var layer := CanvasLayer.new()
	layer.offset = Vector2(20, 30)
	viewport.add_child(layer)
	var doc := WevaDocument.new()
	doc.position = Vector2(17, 11)
	doc.scale = Vector2(1.5, 1.5)
	doc.document_size = Vector2(360, 260)
	doc.use_engine_font = false
	doc.css = "html,body{margin:0}input,textarea{font-size:16px}"
	doc.html = '<input id="field" data-model="Name"><p id="label">{{ Name }}</p><textarea id="area">abcd</textarea><button id="other">Other</button>'
	doc.data = {"Name": "abcd"}
	layer.add_child(doc)
	await get_tree().process_frame
	doc.set_focus("#field")
	doc.set_element_selection("#field", 1, 3)
	var lifecycle: Array[String] = []
	var text_events: Array[String] = []
	doc.composition_started.connect(func(_id, text): lifecycle.append("start:" + text))
	doc.composition_updated.connect(func(_id, text): lifecycle.append("update:" + text))
	doc.composition_ended.connect(func(_id, text): lifecycle.append("end:" + text))
	doc.text_entered.connect(func(_id, text): text_events.append(text))
	check(doc.set_composition("に", 1, 1), "a composition starts in the selected field")
	check(doc.has_composition(), "composition state is observable")
	check(doc.get_element_value("#field") == "aにd", "preedit replaces the selected text")
	check(doc.data.Name == "aにd" and doc.query_text("#label") == "aにd", "provisional input reaches data-model and bound labels")
	check(lifecycle == ["start:bc", "update:に"], "composition signals preserve Unicode and start data")
	check(text_events.is_empty(), "preedit is not reported as committed key text")
	doc.set_composition("日本", 0, 2)
	check(doc.get_element_selection("#field") == Vector2i(1, 7), "Godot character positions convert to UTF-8 byte offsets")
	doc.commit_composition("日本語")
	check(doc.get_element_value("#field") == "a日本語d" and not doc.has_composition(), "commit replaces the preedit once")
	check(text_events == ["日本語"], "committed text is delivered once: " + str(text_events))
	doc.undo()
	doc.update_document(0)
	check(doc.get_element_value("#field") == "abcd" and doc.data.Name == "abcd", "one undo restores the value before the composition")
	doc.redo()
	doc.update_document(0)
	check(doc.get_element_value("#field") == "a日本語d", "redo restores the committed composition")

	doc.set_element_value("#field", "abcd")
	doc.set_element_selection("#field", 1, 3)
	doc.set_composition("nihao", 5, 5)
	text_events.clear()
	for unicode in [0x4f60, 0x597d]:
		var event := InputEventKey.new()
		event.pressed = true
		event.unicode = unicode
		viewport.push_input(event, true)
	check(viewport.is_input_handled(), "IME result keys stay out of gameplay input")
	await get_tree().process_frame
	check(doc.get_element_value("#field") == "a你好d", "a multi-character native key batch replaces one preedit")
	check(not doc.has_composition() and text_events == ["你好"], "the native key batch ends the composition once")
	doc.undo()
	doc.update_document(0)
	check(doc.get_element_value("#field") == "abcd", "native IME result characters share one undo step")

	doc.set_element_selection("#field", 1, 3)
	doc.set_composition("日本", 2, 2)
	doc.commit_composition("")
	check(doc.get_element_value("#field") == "ad", "cancelling removes the preedit as in Chrome")
	doc.undo()
	doc.update_document(0)
	doc.set_element_selection("#field", 1, 3)
	doc.set_composition("日本", 2, 2)
	doc.set_focus("#other")
	check(not doc.has_composition() and doc.get_element_value("#field") == "a日本d", "focus loss keeps the current preedit")
	check(not doc.set_composition("ignored", 0, 0), "buttons do not accept composition text")
	doc.set_focus("#area")
	doc.set_element_selection("#area", 1, 3)
	doc.set_composition("日本", 2, 2)
	check(doc.get_element_value("#area") == "a日本d", "textarea composition uses the same editing path")
	doc.finish_composition()
	check(not doc.has_composition(), "hosts can finish the current preedit without supplying text")
	doc.set_element_attribute("#area", "readonly", "")
	check(not doc.set_composition("ignored", 0, 0), "readonly text rejects composition")
	check(not doc.get_caret_bounds().has_area(), "readonly text has no IME insertion target")

	doc.set_focus("#field")
	var local := doc.get_caret_bounds()
	var window := doc.get_caret_window_bounds()
	check(local.has_area(), "editable input exposes its caret rectangle")
	check(window.position.distance_to(local.position * 1.5 + Vector2(46, 54)) < 0.01, "candidate coordinates include the document, CanvasLayer and viewport container")
	check(window.size.distance_to(local.size * 1.5) < 0.01, "candidate geometry includes canvas scale")
	doc.set_element_attribute("#field", "style", "transform:translate(8px,5px)")
	doc.update_document(0)
	check(doc.get_caret_bounds().position.distance_to(local.position + Vector2(8, 5)) < 0.01, "CSS transforms move the candidate anchor")
	doc.set_composition("preview", 7, 7)
	doc.hide()
	check(not doc.has_composition(), "hiding the document ends composition")
	doc.show()
	doc.set_focus("#field")
	doc.set_composition("preview", 7, 7)
	doc.html = '<input id="fresh">'
	doc.update_document(0)
	check(not doc.has_composition(), "document reload drops stale composition targets")
	container.free()
	print("godot IME integration: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
