extends Node

var checks := 0
var failures := 0

func require(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL delayed transition: ", message)

func _ready() -> void:
	for duration: int in [0, 1]:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.document_size = Vector2(800, 600)
		doc.html = '<div id="a">Panel</div>'
		doc.css = '#a{width:100px;height:20px;background:red;transition:width %ss linear .5s}' % duration
		var draws := [0]
		doc.draw.connect(func(): draws[0] += 1)
		add_child(doc)
		doc.update_document(0)
		doc.set_element_style("#a", "width", "300px")
		doc.update_document(0)
		require(is_equal_approx(doc.query_bounds("#a").size.x, 100), "initial width")
		var rendered := DisplayServer.get_name() != "headless"
		if rendered:
			await get_tree().create_timer(0.05).timeout
			require(draws[0] > 0, "initial draw observed")
		var before: int = draws[0]
		for frame in 8:
			doc.update_document(0.03125)
			if rendered:
				await get_tree().process_frame
		if rendered:
			await get_tree().process_frame
			require(draws[0] == before, "waiting produces no redraws")
		require(is_equal_approx(doc.query_bounds("#a").size.x, 100), "width before delay expires")
		doc.update_document(0.25)
		require(is_equal_approx(doc.query_bounds("#a").size.x, 300 if duration == 0 else 100), "exact delay boundary")
		doc.free()
	print("Delayed transitions: ", checks, " checks, ", failures, " failures")
	get_tree().quit(1 if failures else 0)
