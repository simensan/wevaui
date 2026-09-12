extends Node
var checks := 0
var failures := 0
var doc: WevaDocument
var expected_ms := 0.0
var deferred_done := false
func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", message)
func update_once() -> void:
	doc.update_document(0)
	expected_ms += doc.get_last_update_ms()
func later() -> void:
	update_once()
	update_once()
	deferred_done = true
func _ready() -> void:
	doc = WevaDocument.new()
	doc.html = '<input id="field"><p id="label">Initial</p><button id="first">First</button><button id="second">Second</button>'
	add_child(doc)
	doc.set_process(false)
	var initial_ms := doc.get_total_core_update_ms()
	var initial_count := doc.get_core_update_count()
	for i in 4:
		doc.set_element_value("#field", str(i))
		update_once()
	check(doc.get_core_update_count() - initial_count == 4, "Counts every synchronous core update")
	check(abs(doc.get_total_core_update_ms() - initial_ms - expected_ms) < 0.000001, "Cumulative time equals all updates, not last update")
	var before_read := doc.get_core_update_count()
	doc.get_total_core_update_ms()
	doc.get_last_update_ms()
	check(doc.get_core_update_count() == before_read, "Timing reads do not trigger updates")
	call_deferred("later")
	await get_tree().process_frame
	check(deferred_done, "Frame boundary follows deferred updates")
	check(doc.get_core_update_count() - initial_count == 6, "Both deferred updates included")
	check(abs(doc.get_total_core_update_ms() - initial_ms - expected_ms) < 0.000001, "Deferred time accumulated with synchronous work")
	var settled := doc.get_total_core_update_ms()
	await get_tree().process_frame
	check(doc.get_total_core_update_ms() == settled, "No repeated stale time when updates disabled")
	doc.set_process(true)
	await get_tree().process_frame
	await get_tree().process_frame
	check(doc.get_core_update_count() > initial_count + 6, "Automatic process updates are counted")
	check(doc.get_total_core_update_ms() >= settled, "Automatic time is monotonic")
	doc.set_process(false)
	doc.set_element_style("#label", "width", "137px")
	doc.set_element_attribute("#label", "data-state", "ready")
	var before_attributes := doc.get_core_update_count()
	check(doc.has_element_attribute("[data-state=ready]", "data-state"), "Attribute selector sees pending DOM changes")
	check(doc.get_element_attribute("#label", "data-state") == "ready", "Attribute value is current without layout")
	check(not doc.has_element_attribute("#missing", "data-state"), "Missing element attribute is false")
	check(doc.get_element_attribute("#missing", "data-state").is_empty(), "Missing element value is empty")
	check(doc.get_core_update_count() == before_attributes, "DOM attribute reads do not flush pending layout")
	check(is_equal_approx(doc.query_bounds("#label").size.x, 137.0), "Geometry reads still resolve pending style")
	check(doc.get_core_update_count() == before_attributes + 1, "Geometry read flushes once")
	doc.set_focus("#first")
	doc.update_document(0)
	var before_button_focus := doc.get_core_update_count()
	doc.set_element_style("#second", "width", "151px")
	check(doc.set_focus("#second"), "Button focus changes immediately")
	check(doc.get_core_update_count() == before_button_focus, "Button IME synchronization leaves pending layout unflushed")
	check(doc.get_focused_id() == "second", "Deferred paint retains current button focus")
	check(is_equal_approx(doc.query_bounds("#second").size.x, 151.0), "Explicit button geometry still flushes pending changes")
	doc.set_focus("#field")
	check(doc.get_caret_bounds().size.y > 0, "Text focus still publishes a usable IME caret")
	doc.free()
	print("godot timing: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
