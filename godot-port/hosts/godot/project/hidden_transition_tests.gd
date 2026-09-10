extends Node

var checks := 0
var failures := 0

func require(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL hidden transition: ", message)

func _ready() -> void:
	for target: String in ["#a", "#panel"]:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.document_size = Vector2(800, 600)
		doc.html = '<div id="panel"><div id="a">Panel</div></div>'
		doc.css = '#a{width:100px;height:20px;background:red;transition:width 1s linear}'
		var draws := [0]
		doc.draw.connect(func(): draws[0] += 1)
		add_child(doc)
		doc.update_document(0)
		doc.set_element_style("#a", "width", "300px")
		doc.update_document(0)
		doc.update_document(0.5)
		require(is_equal_approx(doc.query_bounds("#a").size.x, 200), target + " visible transition")
		doc.set_element_style(target, "display", "none")
		doc.update_document(0)
		if DisplayServer.get_name() != "headless":
			await get_tree().create_timer(0.05).timeout
			require(draws[0] > 0, target + " initial draw observed")
			var hidden_draws: int = draws[0]
			for frame in 8:
				doc.update_document(0.01)
				await get_tree().process_frame
			await get_tree().process_frame
			require(draws[0] == hidden_draws, target + " hidden time produces no redraws")
		doc.set_element_style("#a", "width", "400px")
		doc.update_document(0)
		doc.set_element_style(target, "display", "block")
		doc.set_element_style("#a", "width", "500px")
		doc.update_document(0)
		require(is_equal_approx(doc.query_bounds("#a").size.x, 500), target + " latest value on reopening")
		doc.set_element_style("#a", "width", "600px")
		doc.update_document(0)
		doc.update_document(0.5)
		require(is_equal_approx(doc.query_bounds("#a").size.x, 550), target + " subsequent transition")
		doc.free()
	print("Hidden transitions: ", checks, " checks, ", failures, " failures")
	get_tree().quit(1 if failures else 0)
