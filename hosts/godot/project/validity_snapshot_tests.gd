extends Node
var checks := 0
var failures := 0
func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", message)
func _ready() -> void:
	var rows: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://validity_snapshot_cases.json"))
	check(rows is Dictionary and rows.rows.size() == 44, "Complete Chrome snapshot fixture")
	var keys := ["value_missing", "type_mismatch", "pattern_mismatch", "too_long", "too_short", "range_underflow", "range_overflow", "step_mismatch", "bad_input", "custom_error"]
	for row in rows.rows:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.html = row.html
		add_child(doc)
		doc.update_document(0)
		if row.custom: check(doc.set_custom_validity("#c", "Reserved"), "Set custom error")
		var count := doc.get_core_update_count()
		var state := doc.get_element_validity("#c")
		check(state.size() == 12, "Complete validity snapshot")
		check(state.get("valid") == (int(row.flags) == 0), "Validity aggregate")
		check(state.get("will_validate") == row.will, "Validation candidate")
		for bit in range(keys.size()):
			check(state.get(keys[bit]) == ((int(row.flags) & (1 << bit)) != 0), "Validity " + keys[bit])
		check(doc.get_core_update_count() == count, "Snapshot does not flush layout")
		check(doc.get_element_validity("#missing").is_empty(), "Missing selector returns empty")
		doc.free()
	var doc := WevaDocument.new()
	doc.paused = true
	doc.html = '<input id="c" type="number" min="10" max="2" step="2" value="5"><div id="x"></div>'
	add_child(doc)
	doc.update_document(0)
	var state := doc.get_element_validity("#c")
	check(state.range_underflow and state.range_overflow and state.step_mismatch, "Multiple errors")
	var count := doc.get_core_update_count()
	doc.set_element_value("#c", "12")
	state = doc.get_element_validity("#c")
	check(state.range_overflow and not state.range_underflow and not state.step_mismatch, "Pending value visible")
	check(doc.get_core_update_count() == count, "Pending value snapshot avoids layout")
	check(doc.get_element_validity("#x").is_empty(), "Non-control returns empty")
	doc.free()
	print("godot validity snapshot: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
