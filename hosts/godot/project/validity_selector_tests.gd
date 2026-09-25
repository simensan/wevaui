extends Node
var checks := 0
var failures := 0
func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", message)
func _ready() -> void:
	var fixture: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://validity_selector_cases.json"))
	check(fixture.rows.size() == 105, "Complete Chrome validity fixture")
	for row in fixture.rows:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.html = row.html
		add_child(doc)
		if row.custom != null:
			check(doc.set_custom_validity("#" + row.custom, "Reserved"), "Custom validity " + row.name)
		for state in row.states:
			for pseudo in ["valid", "invalid"]:
				var expected: bool = state[pseudo] if row.name != "pattern" else false
				check(doc.has_element("#" + state.id + ":" + pseudo) == expected, row.name + " " + state.id + ":" + pseudo)
		doc.free()
	var doc := WevaDocument.new()
	doc.paused = true
	doc.html = '<form id=f></form><fieldset id=s><input id=c form=f required></fieldset>'
	doc.css = 'form,fieldset{width:100px;min-width:0;padding:0;border:0}form:invalid{width:120px}fieldset:invalid{width:140px}'
	add_child(doc)
	doc.update_document(0)
	check(doc.query_bounds("#f").size.x == 120, "External form initially invalid")
	check(doc.query_bounds("#s").size.x == 140, "Fieldset initially invalid and block sized")
	doc.set_element_value("#c", "ready")
	doc.update_document(0)
	check(doc.query_bounds("#f").size.x == 100, "External form becomes valid")
	check(doc.query_bounds("#s").size.x == 100, "Fieldset becomes valid")
	check(doc.set_custom_validity("#s", "Group error"), "Fieldset accepts custom error")
	var state: Dictionary = doc.get_element_validity("#s")
	check(not state.valid and state.custom_error and not state.will_validate, "Fieldset validity object")
	check(doc.has_element("#s:valid"), "Fieldset own error excluded from aggregate")
	doc.set_custom_validity("#c", "Reserved")
	doc.update_document(0)
	check(doc.query_bounds("#f").size.x == 120, "External form custom invalid")
	check(doc.query_bounds("#s").size.x == 140, "Fieldset custom invalid descendant")
	doc.free()
	print("godot validity selectors: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
