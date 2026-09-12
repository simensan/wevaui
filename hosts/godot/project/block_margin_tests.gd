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
	var fixture: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://block_margin_cases.json"))
	check(fixture.rows.size() == 96 and fixture.live.size() == 3, "Complete Chrome block margin fixture")
	for row in fixture.rows:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.document_size = Vector2(fixture.viewport[0], fixture.viewport[1])
		doc.html = row.html
		doc.css = row.css
		add_child(doc)
		bounds_match(doc, row.bounds, row.name)
		for group in fixture.live:
			if group.base != row.name: continue
			for step in group.steps:
				if step.style == null: doc.remove_element_attribute("#" + step.target, "style")
				else: doc.set_element_attribute("#" + step.target, "style", step.style)
				bounds_match(doc, step.bounds, row.name + " live " + str(step.style))
		doc.free()
	print("godot block margins: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
