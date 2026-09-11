extends Node
var failures: Array[String] = []
func stats(values: Array[float]) -> Dictionary:
	values.sort()
	var total := 0.0
	for value in values: total += value
	return {"mean": total / values.size(), "p50": values[values.size() / 2], "p95": values[ceili(values.size() * 0.95) - 1], "max": values.back()}
func _ready() -> void:
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	DisplayServer.window_set_size(Vector2i(1280, 720))
	var output := OS.get_environment("WEVA_RANGE_PERF_OUT")
	if output.is_empty():
		get_tree().quit(2)
		return
	var rows: Array[Dictionary] = []
	for fields in [12, 48]:
		for style in ["none", "field", "parent", "field_paint", "parent_paint"]:
			for transition in ["same_range", "cross_range"]:
				var doc := WevaDocument.new()
				doc.paused = true
				doc.interactive = false
				doc.document_size = Vector2(1280, 720)
				var markup := '<form id="settings">'
				for i in range(fields):
					markup += '<label>Setting %d<input id="field%d" type="number" min="1" max="10" step="1" value="5"></label>' % [i, i]
				markup += '</form>'
				doc.html = markup
				doc.css = 'form{display:grid;grid-template-columns:repeat(4,240px);width:1000px;gap:4px}label{display:block;height:40px}input{display:block;box-sizing:border-box;min-width:0;width:180px;height:20px}'
				if style == "field": doc.css += 'input:in-range{width:190px}input:out-of-range{width:200px}'
				if style == "parent": doc.css += 'form:has(:out-of-range){width:1020px}'
				if style == "field_paint": doc.css += 'input:in-range{background-color:green}input:out-of-range{background-color:red}'
				if style == "parent_paint": doc.css += 'form{background-color:green}form:has(:out-of-range){background-color:red}'
				add_child(doc)
				doc.update_document(0)
				var paint_target := "#field0" if style == "field_paint" else "#settings"
				var paint_inside := ""
				var paint_outside := ""
				if style.ends_with("_paint"):
					paint_inside = doc.get_computed_style(paint_target, "background-color")
					doc.set_element_value("#field0", "11")
					doc.update_document(0)
					paint_outside = doc.get_computed_style(paint_target, "background-color")
					if paint_inside.is_empty() or paint_inside == paint_outside: failures.append("Distinct error highlight: " + style)
					doc.set_element_value("#field0", "5")
					doc.update_document(0)
				var api: Array[float] = []
				var mutation: Array[float] = []
				var updates: Array[float] = []
				for sample in range(300):
					var outside: bool = transition == "cross_range" and sample % 2 == 1
					var value := "11" if outside else ("5" if sample % 2 == 0 else "6")
					var count := doc.get_core_update_count()
					var start := Time.get_ticks_usec()
					doc.set_element_value("#field0", value)
					var mutation_ms := (Time.get_ticks_usec() - start) / 1000.0
					doc.update_document(0)
					var api_ms := (Time.get_ticks_usec() - start) / 1000.0
					var update_count := float(doc.get_core_update_count() - count)
					# Assertions and geometry reads are outside the measured interval.
					var width: float = (200.0 if outside else 190.0) if style == "field" else 180.0
					if doc.query_bounds("#field0").size.x != width: failures.append("Field style: " + style)
					var parent_width := 1020.0 if style == "parent" and outside else 1000.0
					if doc.query_bounds("#settings").size.x != parent_width: failures.append("Parent style: " + style)
					if doc.has_element("#field0:out-of-range") != outside: failures.append("Range matching")
					if style.ends_with("_paint") and doc.get_computed_style(paint_target, "background-color") != (paint_outside if outside else paint_inside): failures.append("Paint style: " + style)
					if doc.get_element_value("#field0") != value: failures.append("Current field value")
					if sample >= 60:
						api.append(api_ms)
						mutation.append(mutation_ms)
						updates.append(update_count)
					await get_tree().process_frame
				rows.append({"fields": fields, "style": style, "transition": transition, "api_ms": stats(api), "mutation_ms": stats(mutation), "core_updates": stats(updates)})
				doc.free()
	var file := FileAccess.open(output, FileAccess.WRITE)
	file.store_string(JSON.stringify({"rows": rows, "failures": failures, "passed": failures.is_empty(), "samples": 240, "warmups": 60, "scope": "One numeric value mutation and explicit update per frame; paused document, native input disabled, 1280x720. Setup, assertions, draw submission and GPU excluded."}, "  "))
	print("Range performance: ", "PASS" if failures.is_empty() else "FAIL")
	get_tree().quit(0 if failures.is_empty() else 1)
