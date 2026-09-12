extends Node

var viewport: SubViewport

func key(code: Key) -> void:
	for pressed in [true, false]:
		var event := InputEventKey.new()
		event.keycode = code
		event.pressed = pressed
		viewport.push_input(event, true)

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(500, 400)
	add_child(viewport)
	var oracle = JSON.parse_string(FileAccess.get_file_as_string("res://number_step_cases.json"))
	var failures := 0
	var checks := 0
	for row in oracle.rows:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.document_size = Vector2(500, 400)
		doc.size = Vector2(500, 400)
		var markup := '<input id="n" type="number"'
		for attr in row.attrs:
			if attr != "current":
				markup += ' %s="%s"' % [attr, str(row.attrs[attr]).xml_escape(true)]
		doc.html = markup + '>'
		doc.css = 'input{width:200px;height:30px}'
		viewport.add_child(doc)
		doc.update_document(0)
		if row.attrs.has("current"):
			doc.set_element_value("#n", row.attrs.current)
		var events: Array[String] = []
		doc.value_changed.connect(func(_id, _value): events.append("input"))
		doc.value_committed.connect(func(_id, _value): events.append("change"))
		doc.set_focus("#n")
		checks += 1
		if doc.get_element_value("#n") != row.before:
			failures += 1
			printerr("FAIL number initial value: ", JSON.stringify(row))
		key(KEY_UP if row.key == "ArrowUp" else KEY_DOWN)
		checks += 1
		var actual := doc.get_element_value("#n")
		if actual != row.value:
			failures += 1
			printerr("FAIL number step: ", JSON.stringify(row), " actual=", actual)
		checks += 1
		if events != row.events:
			failures += 1
			printerr("FAIL number events: ", JSON.stringify(row), " actual=", events)
		doc.free()
	var bound := WevaDocument.new()
	bound.paused = true
	bound.document_size = Vector2(500, 400)
	bound.data = {"Quantity": 5}
	bound.html = '<input id="quantity" type="number" min="1" max="9" step="2" data-model="Quantity"><p id="echo">{{ Quantity }}</p><button id="done">Done</button>'
	viewport.add_child(bound)
	bound.update_document(0)
	bound.set_focus("#quantity")
	var commits: Array[String] = []
	bound.value_committed.connect(func(_id, value): commits.append(value))
	for expected in [7, 9, 9]:
		key(KEY_UP)
		bound.update_document(0)
		checks += 1
		if bound.data.Quantity != expected or typeof(bound.data.Quantity) != TYPE_INT or bound.get_element_text("#echo") != str(expected):
			failures += 1
			printerr("FAIL numeric model and rendered text update: ", expected, " actual=", bound.data)
	bound.set_focus("#done")
	checks += 1
	if commits != ["7", "9"]:
		failures += 1
		printerr("FAIL number commit/limit/blur events: ", commits)
	bound.set_element_attribute("#quantity", "min", "0")
	bound.set_element_attribute("#quantity", "max", "20")
	bound.set_element_attribute("#quantity", "step", "3")
	bound.set_focus("#quantity")
	key(KEY_DOWN)
	bound.update_document(0)
	checks += 1
	if bound.data.Quantity != 6 or bound.get_element_text("#echo") != "6":
		failures += 1
		printerr("FAIL number step uses live constraints: ", bound.data)
	bound.data = {"Quantity": 12}
	bound.update_document(0)
	key(KEY_UP)
	bound.update_document(0)
	checks += 1
	if bound.data.Quantity != 15 or bound.get_element_text("#echo") != "15":
		failures += 1
		printerr("FAIL number step uses replaced model: ", bound.data)
	bound.free()
	print("number steps: ", checks, " checks, ", failures, " failures")
	get_tree().quit(1 if failures else 0)
