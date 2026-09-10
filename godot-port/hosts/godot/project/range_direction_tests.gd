extends Node

func _ready() -> void:
	var oracle = JSON.parse_string(FileAccess.get_file_as_string("res://range_direction_cases.json"))
	var keys := {"ArrowLeft":KEY_LEFT,"ArrowRight":KEY_RIGHT,"ArrowUp":KEY_UP,"ArrowDown":KEY_DOWN,"Home":KEY_HOME,"End":KEY_END,"PageUp":KEY_PAGEUP,"PageDown":KEY_PAGEDOWN}
	var failures := 0
	for row in oracle.rows:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.document_size = Vector2(500,400)
		doc.size = Vector2(500,400)
		doc.html = '<input id="r" type="range" min="0" max="100" value="50">'
		var horizontal: bool = row.mode == "horizontal-tb"
		doc.css = '#r{position:absolute;left:40px;top:40px;width:%dpx;height:%dpx;writing-mode:%s;direction:%s}' % [200 if horizontal else 30,30 if horizontal else 200,row.mode,row.direction]
		add_child(doc)
		doc.update_document(0)
		doc.set_focus("#r")
		if row.has("key"):
			doc.send_key(keys[row.key])
		else:
			var rect := doc.query_bounds("#r")
			var point := rect.get_center()
			if horizontal: point.x = rect.position.x + clampf(rect.size.x * row.fraction,1,rect.size.x-1)
			else: point.y = rect.position.y + clampf(rect.size.y * row.fraction,1,rect.size.y-1)
			var motion := InputEventMouseMotion.new()
			motion.position = point
			get_viewport().push_input(motion,true)
			for down in [true,false]:
				var event := InputEventMouseButton.new()
				event.position = point
				event.button_index = MOUSE_BUTTON_LEFT
				event.pressed = down
				get_viewport().push_input(event,true)
		var actual := float(doc.get_element_value("#r"))
		if actual != row.value:
			failures += 1
			printerr("FAIL range direction: ",JSON.stringify(row)," actual=",actual)
		doc.free()
	print("range direction: ",oracle.rows.size()," checks, ",failures," failures")
	get_tree().quit(1 if failures else 0)
