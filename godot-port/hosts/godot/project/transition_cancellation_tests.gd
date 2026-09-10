extends Node

var checks := 0
var failures := 0

func require(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL transition cancellation: ", message)

func _ready() -> void:
	for value: String in ["none", "opacity", "0s"]:
		for retarget: bool in [false, true]:
			var doc := WevaDocument.new()
			doc.paused = true
			doc.document_size = Vector2(800, 600)
			doc.html = '<div id="a">Panel</div>'
			doc.css = '#a{width:100px;height:20px;background:red;transition:width 1s linear}'
			var draws := [0]
			doc.draw.connect(func(): draws[0] += 1)
			add_child(doc)
			doc.update_document(0)
			doc.set_element_style("#a", "width", "300px")
			doc.update_document(0)
			doc.update_document(0.5)
			require(is_equal_approx(doc.query_bounds("#a").size.x, 200), "initial midpoint")
			doc.set_element_style("#a", "transition-duration" if value == "0s" else "transition-property", value)
			if retarget:
				doc.set_element_style("#a", "width", "400px")
			doc.update_document(0)
			var continues := value == "0s" and not retarget
			var target := 400 if retarget else 300
			require(is_equal_approx(doc.query_bounds("#a").size.x, 200 if continues else target), "immediate value")
			var rendered := DisplayServer.get_name() != "headless"
			if rendered:
				await get_tree().create_timer(0.05).timeout
				require(draws[0] > 0, "initial render observed")
			var before: int = draws[0]
			doc.update_document(0.25)
			require(is_equal_approx(doc.query_bounds("#a").size.x, 250 if continues else target), "later value")
			if rendered:
				await get_tree().create_timer(0.05).timeout
				require(draws[0] > before if continues else draws[0] == before, "only continuing transition redraws")
			doc.free()
	print("Transition cancellation: ", checks, " checks, ", failures, " failures")
	get_tree().quit(1 if failures else 0)
