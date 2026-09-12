extends Node
var checks := 0
var failures := 0
var doc: WevaDocument
var action := ""
var events: Array[String] = []
var submissions := 0
var handler_updates := 0
func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", message)
func on_invalid(id: String) -> void:
	events.append(id)
	if action == "roundtrip": doc.set_focus("#save")
	if action in ["update", "roundtrip"]:
		doc.update_document(0)
		handler_updates = doc.get_core_update_count()
	if action == "cancel": check(doc.prevent_default(), "Cancelable invalid")
	if id == "a":
		if action == "fix": doc.set_element_value("#b", "ok")
		if action == "disable": doc.set_element_attribute("#b", "disabled", "")
		if action == "remove": doc.remove_element("#b")
		if action == "reassociate": doc.set_element_attribute("#b", "form", "other")
func _ready() -> void:
	var fixture: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://explicit_validity_cases.json"))
	check(fixture is Dictionary and fixture.rows.size() == 24, "Complete Chrome check/report fixture")
	for row in fixture.rows:
		action = row.action
		events.clear()
		doc = WevaDocument.new()
		doc.paused = true
		doc.html = '<input id="external" form="f" required><form id="f" novalidate><input id="a" required><input id="b" required><button id="save">Save</button></form><form id="other"></form>'
		add_child(doc)
		doc.update_document(0)
		doc.element_invalid.connect(on_invalid)
		doc.form_submitted.connect(func(_id: String): submissions += 1)
		doc.set_focus("#save")
		var valid: bool = doc.check_validity("#" + row.scope) if row.method == "checkValidity" else doc.report_validity("#" + row.scope)
		check(valid == row.valid, "Validation result")
		check(events == Array(row.events, TYPE_STRING, "", null), "Invalid event order and mutation handling")
		check(doc.get_focused_id() == row.focus, "Check/report focus")
		doc.free()
	# Save-button callbacks run inside the event pump. Invalid handlers follow
	# that callback in the same outer drain, rather than recursively pumping.
	for report in [false, true]:
		doc = WevaDocument.new()
		doc.paused = true
		doc.html = '<form id="f"><input id="a" required><button id="save" type="button">Save</button></form>'
		add_child(doc)
		doc.update_document(0)
		action = "none"
		events.clear()
		doc.element_invalid.connect(on_invalid)
		var clicked := [false]
		doc.element_clicked.connect(func(id: String):
			if id != "save": return
			clicked[0] = true
			check(not (doc.report_validity("#f") if report else doc.check_validity("#f")), "Save callback rejects invalid form")
			check(events.is_empty(), "No recursive event pump inside click handler")
		)
		doc.set_focus("#save")
		doc.send_key(KEY_ENTER, true)
		doc.send_key(KEY_ENTER, false)
		check(clicked[0], "Save callback ran")
		check(events == ["a"], "Save callback drains invalid event")
		check(doc.get_focused_id() == ("a" if report else "save"), "Save callback reporting focus")
		doc.free()
	# INVALID notification alone is not a visual change. A reporting focus
	# change after a handler's update must still schedule its final frame.
	for mode in ["check", "report", "roundtrip"]:
		var report: bool = mode != "check"
		doc = WevaDocument.new()
		doc.paused = true
		doc.interactive = false
		doc.html = '<form id="f"><button id="a">Invalid</button><button id="save" type="button">Save</button></form>'
		doc.css = '#a{box-sizing:border-box;width:100px}#a:focus{width:177px}'
		add_child(doc)
		doc.update_document(0)
		doc.set_custom_validity("#a", "Reserved")
		doc.set_focus("#a" if mode == "roundtrip" else "#save")
		doc.update_document(0)
		action = "roundtrip" if mode == "roundtrip" else "update" if report else "none"
		doc.element_invalid.connect(on_invalid)
		var before := doc.get_core_update_count()
		check(not (doc.report_validity("#f") if report else doc.check_validity("#f")), "Invalid custom control")
		await get_tree().process_frame
		await get_tree().process_frame
		if report:
			check(doc.get_core_update_count() > handler_updates, "Default focus schedules update after handler flush")
			check(absf(doc.query_bounds("#a").size.x - 177.0) < 0.01, "Reported focus style reaches layout")
		else:
			check(doc.get_core_update_count() == before, "Check-only invalid notification does not refresh settled UI")
		doc.free()
	# Programmatic reporting from a native Godot button must transfer keyboard
	# focus to this UI, not only change its internal HTML focus pointer.
	doc = WevaDocument.new()
	doc.paused = true
	doc.html = '<input id="a" required>'
	add_child(doc)
	doc.update_document(0)
	var native_button := Button.new()
	add_child(native_button)
	native_button.grab_focus()
	check(native_button.has_focus(), "Native button initially owns focus")
	check(not doc.report_validity("#a"), "External report detects invalid field")
	check(doc.has_focus(), "Reporting transfers native keyboard focus")
	check(doc.get_focused_id() == "a", "Reporting transfers HTML focus")
	native_button.free()
	doc.free()
	check(submissions == 0, "Explicit validation never submits")
	print("godot explicit validity: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
