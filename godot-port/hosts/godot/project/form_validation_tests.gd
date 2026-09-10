extends Node

var checks := 0
var failures := 0
var doc: WevaDocument
var invalids := 0
var markup_invalids := 0
var submits := 0
var expected_message := ""
var veto_reporting := true

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func invalid_handler(id: String) -> void:
	markup_invalids += 1
	check(id == "c", "Invalid markup target")

func invalid_signal(id: String) -> void:
	invalids += 1
	check(id == "c", "Invalid signal target")
	check(doc.get_custom_validity("#c") == expected_message, "Full custom message inside invalid handler")
	doc.update_document(0)
	check(doc.has_element_attribute("#d", "open"), "Nested update cannot close failed form")
	if veto_reporting: check(doc.prevent_default(), "Invalid reporting can be canceled")
	doc.send_key(KEY_ENTER, false)
	doc.send_key(KEY_ENTER, true)
	doc.send_key(KEY_ENTER, false)

func submitted(id: String) -> void:
	submits += 1
	check(id == "f", "Submit signal target")

func prepare(control: String) -> void:
	invalids = 0
	markup_invalids = 0
	submits = 0
	expected_message = ""
	doc = WevaDocument.new()
	doc.document_size = Vector2(640, 480)
	doc.paused = true
	doc.controller = self
	doc.html = '<dialog id="d"><form id="f" method="dialog">%s<button id="s" value="accepted">OK</button></form></dialog>' % control
	add_child(doc)
	doc.update_document(0)
	doc.element_invalid.connect(invalid_signal)
	doc.form_submitted.connect(submitted)

func submit(rejected: bool, already_open: bool = false) -> void:
	if not already_open: check(doc.show_modal_dialog("#d"), "Open form dialog")
	doc.update_document(0)
	doc.set_focus("#s")
	doc.send_key(KEY_ENTER, true)
	doc.send_key(KEY_ENTER, false)
	doc.update_document(0)
	check(invalids == int(rejected), "Exactly one invalid signal; reentry suppressed")
	check(markup_invalids == int(rejected), "Invalid markup handler")
	check(submits == int(not rejected), "Invalid controls suppress submit")
	check(doc.has_element_attribute("#d", "open") == rejected, "Dialog submission result")
	check(not doc.prevent_default(), "No cancel context after dispatch")
	if rejected:
		check(doc.get_focused_id() == ("s" if veto_reporting else "c"), "Default reporting respects veto")
	else:
		check(doc.get_dialog_return_value("#d") == "accepted", "Form button result")
	doc.free()

func _ready() -> void:
	for tag in ["input", "textarea", "select", "button"]:
		for action in ["keep", "clear", "reset", "disable"]:
			prepare('<%s id="c" on-invalid="invalid_handler">%s</%s>' % [tag, '<option>Yes</option>' if tag == "select" else "", tag])
			expected_message = "Réservé 🐎".repeat(100)
			check(doc.set_custom_validity("#c", expected_message), "Set custom validity")
			check(doc.get_custom_validity("#c") == expected_message, "Long Unicode custom message")
			if action == "clear":
				expected_message = ""
				doc.set_custom_validity("#c", "")
			if action == "reset": doc.reset_form("#f")
			if action == "disable": doc.set_element_attribute("#c", "disabled", "")
			submit(action in ["keep", "reset"])
	for value in ["", "yes"]:
		veto_reporting = false
		prepare('<input id="c" required on-invalid="invalid_handler">')
		doc.set_element_value("#c", value)
		submit(value == "")
	for value in ["0.3", "0.31", "-1", "2"]:
		prepare('<input id="c" type="number" min="0" max="1" step="0.1" on-invalid="invalid_handler">')
		doc.set_element_value("#c", value)
		submit(value != "0.3")
	for row in [
		["email", "player@camp", false], ["email", "player", true],
		["email", "a@-camp", true], ["email", "a@bücher.de", true],
		["url", "https://camp.example", false], ["url", "/relative", true],
		["url", "steam://connect/127.0.0.1", false], ["url", "https://x:65536", true],
		["url", "https://[::1]/", false], ["url", "https://[bad]/", true],
		["url", "https://bücher.de", false], ["url", "http://a b", true]]:
		for bypass in [false, true]:
			prepare('<input id="c" type="%s" on-invalid="invalid_handler">' % row[0])
			doc.set_element_value("#c", row[1])
			if bypass: doc.set_element_attribute("#f", "novalidate", "")
			submit(row[2] and not bypass)
	for tag in ["input", "textarea"]:
		for action in ["typed", "script", "same-script", "other-script", "undo", "redo", "composition", "composition-script", "composition-cancel", "composition-finish", "reset", "max-shrink"]:
			prepare('<%s id="c" minlength="5" on-invalid="invalid_handler"></%s>' % [tag, tag])
			check(doc.show_modal_dialog("#d"), "Open editing form")
			doc.update_document(0)
			doc.set_focus("#c")
			if action in ["composition", "composition-script", "composition-cancel", "composition-finish"]:
				check(doc.set_composition("ab", 2, 2), "Set length-validation preedit")
				if action == "composition-finish":
					check(doc.finish_composition(), "Finish length-validation preedit")
				else:
					check(doc.commit_composition("" if action == "composition-cancel" else "ab"), "Commit length-validation preedit")
				check(not doc.has_composition(), "Composition ends before validation")
			elif action in ["script", "undo", "redo"]:
				doc.set_element_value("#c", "ab")
			else:
				doc.send_text("ab")
			if action in ["same-script", "composition-script"]: doc.set_element_value("#c", "ab")
			if action == "other-script": doc.set_element_value("#c", "z")
			if action in ["undo", "redo"]:
				doc.send_text("z")
				check(doc.undo(), "Undo user edit")
				if action == "redo": check(doc.redo(), "Redo user edit")
			if action == "reset": doc.reset_form("#f")
			if action == "max-shrink":
				doc.set_element_attribute("#c", "minlength", "0")
				doc.set_element_attribute("#c", "maxlength", "1")
			var rejected: bool = action not in ["script", "other-script", "reset", "composition-cancel"]
			if action in ["same-script", "composition-script"]: rejected = tag == "textarea"
			submit(rejected, true)
	var temporal: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://form_validation_temporal.json"))
	check(temporal is Dictionary, "Load temporal browser fixture")
	if temporal is Dictionary:
		check(temporal["values"].size() == 71, "Complete temporal value fixture")
		check(temporal["constraints"].size() == 275, "Complete temporal constraint fixture")
		for row in temporal["values"]:
			prepare('<input id="c" type="%s" step="any" on-invalid="invalid_handler">' % row.type)
			doc.set_element_value("#c", row.raw)
			check(doc.get_element_value("#c") == row.value, "Temporal value sanitation: " + row.type + " " + row.raw)
			submit(false)
		for row in temporal["constraints"]:
			for bypass in [false, true]:
				prepare('<input id="c" type="%s" on-invalid="invalid_handler">' % row.type)
				for attribute in ["min", "max", "step"]:
					if row.get(attribute) != null: doc.set_element_attribute("#c", attribute, row[attribute])
				if row.get("initial") != null: doc.set_element_attribute("#c", "value", row.initial)
				doc.set_element_value("#c", row.raw)
				if bypass: doc.set_element_attribute("#f", "novalidate", "")
				var rejected: bool = not bypass and (row.get("underflow", false) or row.get("overflow", false) or row.get("mismatch", false))
				submit(rejected)
	var numbers: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://form_validation_numbers.json"))
	check(numbers is Dictionary, "Load number editing browser fixture")
	if numbers is Dictionary:
		check(numbers["rows"].size() == 580, "Complete number editing fixture")
		for row in numbers["rows"]:
			prepare('<input id="c" type="number" step="any" on-invalid="invalid_handler">')
			check(doc.show_modal_dialog("#d"), "Open number editor")
			doc.update_document(0)
			doc.set_element_value("#c", row.initial)
			var start: int = row.initial.length() if row.place == "end" else mini(2, row.initial.length()) if row.place == "middle" else 0
			var end: int = row.initial.length() if row.place == "all" else start
			check(doc.set_element_selection("#c", start, end), "Place number caret")
			doc.send_text(row.text)
			check(doc.get_element_value("#c") == row.value, "Number edit value: %s %s %s" % [row.initial, row.place, row.text])
			submit(row.bad, true)
	var exponents: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://form_validation_exponents.json"))
	check(exponents is Dictionary, "Load exponent browser fixture")
	if exponents is Dictionary:
		check(exponents.rows.size() == 65 and exponents.excluded_embedded_nul_rows == 5, "Complete representable exponent fixture")
		for row in exponents.rows:
			prepare('<input id="c" type="number" on-invalid="invalid_handler">')
			if row.attr == "current": doc.set_element_value("#c", row.raw)
			else:
				doc.set_element_attribute("#c", row.attr, row.raw)
				if row.attr != "value": doc.set_element_value("#c", "-1" if row.attr == "min" else "1.5")
			check(doc.get_element_value("#c") == row.sanitized, "Exponent sanitization: " + row.attr)
			submit(row.underflow or row.overflow or row.mismatch)
	print("godot form validation: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
