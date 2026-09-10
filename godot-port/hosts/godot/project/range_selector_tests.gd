extends Node
var checks := 0
var failures := 0
func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", message)
func _ready() -> void:
	var fixture: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://range_selector_cases.json"))
	check(fixture.rows.size() == 195, "Complete Chrome range fixture")
	for row in fixture.rows:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.html = "<input id=c>"
		add_child(doc)
		for key in row.attrs: doc.set_element_attribute("#c", key, row.attrs[key])
		check(doc.has_element("#c:in-range") == row.in_range, "In range " + str(row.attrs))
		check(doc.has_element("#c:out-of-range") == row.out_of_range, "Out of range " + str(row.attrs))
		doc.free()
	var doc := WevaDocument.new()
	doc.paused = true
	doc.html = '<section id=p><input id=c type=number min=2 max=8 value=5></section>'
	doc.css = 'input{width:100px;min-width:0;box-sizing:border-box}input:in-range{width:120px}input:out-of-range{width:160px}section:has(:out-of-range){width:210px}section{width:200px}'
	add_child(doc)
	doc.update_document(0)
	check(doc.query_bounds("#c").size.x == 120, "Initial range style")
	doc.set_element_value("#c", "9")
	doc.update_document(0)
	check(doc.query_bounds("#c").size.x == 160, "Changed value style")
	check(doc.query_bounds("#p").size.x == 210, "Parent has style")
	doc.set_element_value("#c", "5")
	doc.update_document(0)
	check(doc.query_bounds("#c").size.x == 120, "Restored value style")
	check(doc.query_bounds("#p").size.x == 200, "Restored parent style")
	doc.free()
	print("godot range selectors: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
