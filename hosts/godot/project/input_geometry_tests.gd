extends Node
var checks := 0
var failures := 0
func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", message)
func _ready() -> void:
	get_window().size = Vector2i(400, 300)
	var doc := WevaDocument.new()
	doc.document_size = Vector2(400, 300)
	doc.html = '<button id=a>A</button><button id=b>B</button>'
	doc.css = 'html,body{margin:0}body{display:flex;align-items:start}button{width:100px;height:30px;padding:0;border:0}#a:hover{width:200px}#a:active{height:70px}'
	add_child(doc)
	doc.set_process(false)
	doc.update_document(0)
	await get_tree().process_frame
	var clicks: Array = []
	doc.element_clicked.connect(func(id): clicks.append(id))
	var motion := InputEventMouseMotion.new()
	motion.position = Vector2(10, 10)
	motion.global_position = motion.position
	get_viewport().push_input(motion, true)
	get_viewport().push_input(motion, true)
	var geometry_count := doc.get_core_update_count()
	get_viewport().push_input(motion, true)
	get_viewport().push_input(motion, true)
	check(doc.get_core_update_count() == geometry_count, "Repeated routing reuses current geometry while paint is pending")
	var down := InputEventMouseButton.new()
	down.position = Vector2(150, 10)
	down.global_position = down.position
	down.button_index = MOUSE_BUTTON_LEFT
	down.button_mask = MOUSE_BUTTON_MASK_LEFT
	down.pressed = true
	get_viewport().push_input(down, true)
	var up := InputEventMouseButton.new()
	up.position = Vector2(150, 60)
	up.global_position = up.position
	up.button_index = MOUSE_BUTTON_LEFT
	up.pressed = false
	get_viewport().push_input(up, true)
	check(clicks == ["a"], "Hover width and active height affect the next event before final paint")
	doc.update_document(0)
	check(doc.get_focused_id() == "a", "Input geometry preserves focused hit target")
	check(doc.query_bounds("#b").position.x >= 100, "Following sibling retains valid layout")
	doc.set_element_style("#a", "background", "#f80")
	get_viewport().push_input(motion, true)
	get_viewport().push_input(motion, true)
	var before_paused := doc.get_core_update_count()
	doc.paused = true
	doc.set_process(true)
	await get_tree().process_frame
	check(doc.get_core_update_count() > before_paused, "Paused automatic processing still publishes pending paint")
	doc.set_process(false)
	doc.free()
	print("godot input geometry: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
