extends Node
var checks := 0
var failures := 0
func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", message)
func bounds_match(doc: WevaDocument, rows: Array, label: String) -> void:
	doc.update_document(0)
	for expected in rows:
		var rect := doc.query_bounds("#" + expected.id)
		var actual := [rect.position.x,rect.position.y,rect.size.x,rect.size.y]
		for i in range(4):
			var key: String = ["x","y","width","height"][i]
			check(abs(actual[i] - expected[key]) < 0.001, label + "/" + expected.id + "/" + key)
func _ready() -> void:
	var fixture: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://multicol_sizing_cases.json"))
	check(fixture.rows.size() == 28 and fixture.live.size() == 16, "Complete Chrome multicol fixture")
	for row in fixture.rows:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.document_size = Vector2(fixture.viewport[0], fixture.viewport[1])
		doc.html = row.html
		doc.css = row.css
		add_child(doc)
		bounds_match(doc, row.bounds, row.name)
		if row.name == "width-limits-count":
			for step in fixture.live:
				if step.style == null: doc.remove_element_attribute("#m", "style")
				else: doc.set_element_attribute("#m", "style", step.style)
				bounds_match(doc, step.bounds, "live " + str(step.style))
		doc.free()
	var spans: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://column_span_cases.json"))
	check(spans.rows.size() == 216, "Complete Chrome direct column-span fixture")
	for row in spans.rows:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.document_size = Vector2(640, 480)
		doc.html = row.html
		doc.css = row.css
		add_child(doc)
		bounds_match(doc, row.boxes, row.name)
		doc.free()
	print("godot multicol sizing: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
