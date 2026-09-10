extends Node
var invalid_events := 0
var cancel_events := false
var failures: Array[String] = []
var doc: WevaDocument
func on_invalid(_id: String) -> void:
	invalid_events += 1
	if cancel_events: doc.prevent_default()
func stats(values: Array[float]) -> Dictionary:
	values.sort()
	var total := 0.0
	for value in values: total += value
	return {"mean": total / values.size(), "p50": values[values.size() / 2], "p95": values[ceili(values.size() * 0.95) - 1], "max": values.back()}
func _ready() -> void:
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	DisplayServer.window_set_size(Vector2i(1280, 720))
	var output := OS.get_environment("WEVA_VALIDATION_PERF_OUT")
	if output.is_empty():
		get_tree().quit(2)
		return
	var native_input := OS.get_environment("WEVA_VALIDATION_DISABLE_NATIVE_INPUT") != "1"
	var rows: Array[Dictionary] = []
	for fields in [12, 48]:
		for scenario in ["snapshot", "check_valid", "check_invalid", "report_invalid", "report_canceled", "check_disabled"]:
			var invalid: bool = scenario in ["check_invalid", "report_invalid", "report_canceled", "check_disabled"]
			cancel_events = scenario == "report_canceled"
			doc = WevaDocument.new()
			doc.paused = true
			doc.interactive = native_input
			doc.document_size = Vector2(1280, 720)
			var markup := '<form id="settings"><fieldset %s>' % ("disabled" if scenario == "check_disabled" else "")
			for i in range(fields):
				var kind: String = ["text", "number", "email"][i % 3]
				var value: String = "" if invalid else ["Morgan", "4", "user@camp.test"][i % 3]
				markup += '<label for="field%d">Setting %d</label><input id="field%d" type="%s" required value="%s" %s>' % [i, i, i, kind, value, 'min="1" max="10" step="1"' if kind == "number" else ""]
			markup += '</fieldset><button id="save" type="button">Save</button></form>'
			doc.html = markup
			doc.css = 'form{width:600px}input{display:block;width:220px;height:20px}label{display:block}'
			add_child(doc)
			doc.update_document(0)
			doc.element_invalid.connect(on_invalid)
			var api_ms: Array[float] = []
			var settle_ms: Array[float] = []
			var core_updates: Array[float] = []
			for sample in range(300):
				doc.set_focus("#save")
				doc.update_document(0)
				invalid_events = 0
				var before := doc.get_core_update_count()
				var started := Time.get_ticks_usec()
				var valid := true
				if scenario == "snapshot":
					var snapshot := doc.get_element_validity("#field0")
					valid = snapshot.get("valid", false)
				elif scenario.begins_with("report"):
					valid = doc.report_validity("#settings")
				else:
					valid = doc.check_validity("#settings")
				var elapsed := (Time.get_ticks_usec() - started) / 1000.0
				started = Time.get_ticks_usec()
				doc.update_document(0)
				var settle := (Time.get_ticks_usec() - started) / 1000.0
				var updates := float(doc.get_core_update_count() - before)
				var expected_invalid: bool = invalid and scenario != "check_disabled"
				if valid == expected_invalid or invalid_events != (fields if expected_invalid else 0):
					failures.append("%s/%d unexpected result or invalid event count" % [scenario, fields])
				var expected_focus := "field0" if scenario == "report_invalid" else "save"
				if doc.get_focused_id() != expected_focus: failures.append("Unexpected focus: " + scenario)
				if sample >= 60:
					api_ms.append(elapsed)
					settle_ms.append(settle)
					core_updates.append(updates)
				await get_tree().process_frame
			rows.append({"fields": fields, "scenario": scenario, "api_ms": stats(api_ms), "settle_update_ms": stats(settle_ms), "core_update_count": stats(core_updates)})
			doc.free()
	var file := FileAccess.open(output, FileAccess.WRITE)
	file.store_string(JSON.stringify({"native_input": native_input, "rows": rows, "failures": failures, "passed": failures.is_empty(), "samples": 240, "warmups": 60, "scope": "Paused document, validation call including invalid handlers, followed by explicit host update; excludes setup, frame drawing and GPU."}, "  "))
	print("Validation performance: ", "PASS" if failures.is_empty() else "FAIL")
	get_tree().quit(0 if failures.is_empty() else 1)
