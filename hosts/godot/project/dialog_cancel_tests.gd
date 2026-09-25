extends Node

var checks := 0
var failures := 0
var doc: WevaDocument
var mode := ""
var cancels := 0
var closes := 0
var signal_cancels := 0
var modal := false
var closed_result: Variant = null

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func cancelled(id: String) -> void:
	cancels += 1
	check(id == "d", "Cancel target")
	check(doc.has_element_attribute("#d", "open"), "Handler runs while open")
	if mode == "prevent" or mode == "update-prevent":
		if mode == "update-prevent":
			doc.update_document(0)
			doc.query_bounds("#d")
			check(doc.has_element_attribute("#d", "open"), "Nested updates do not apply the default early")
		check(doc.prevent_default(), "Handler can veto close")
	elif mode == "result-set":
		doc.set_dialog_return_value("#d", "handler")
	elif mode == "result-close":
		doc.close_dialog("#d", "handler-close")
	elif mode == "result-prevent":
		check(doc.prevent_default(), "Result request veto")
	elif mode == "reopen":
		doc.close_dialog("#d")
		if modal: doc.show_modal_dialog("#d")
		else: doc.show_dialog("#d")
		doc.update_document(0)
	elif mode == "close":
		doc.close_dialog("#d")
	elif mode == "reload":
		doc.html = '<dialog id="d" open>Replacement</dialog>'
		doc.update_document(0)
	elif mode == "remove":
		doc.remove_element("#d")

func cancel_signal(id: String) -> void:
	signal_cancels += 1
	check(id == "d", "Cancel signal target")
	if mode == "signal-prevent":
		doc.update_document(0)
		check(doc.prevent_default(), "Signal can veto after nested update")

func closed(id: String) -> void:
	closes += 1
	check(id == "d", "Close signal target")

func result_closed(id: String) -> void:
	check(id == "d", "Result close signal target")
	closed_result = doc.get_dialog_return_value("#d")

func check_popover_outside() -> void:
	var fixture: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://popover_outside_chrome.json"))
	for row in fixture.rows:
		var panel := WevaDocument.new()
		panel.document_size = Vector2(640, 480)
		panel.html = row.html
		panel.css = row.css
		panel.update_document(0)
		panel.show_popover("#a")
		if row.nested: panel.show_popover("#b")
		if row.manual: panel.show_popover("#m")
		if row.action == "keyboard":
			panel.set_focus("#outside")
			panel.send_key(KEY_ENTER)
		else:
			var down := Vector2(row.down[0], row.down[1])
			var up := Vector2(row.up[0], row.up[1])
			panel.set_pointer(down, 0)
			panel.set_pointer(down, 1)
			panel.set_pointer(up, 1)
			panel.set_pointer(up, 0)
		check(panel.query_all_ids(":popover-open") == PackedStringArray(row.open), "Popover dismissal " + str(row.nested) + " " + str(row.manual) + " " + row.action)
		panel.free()

func check_popover_focus() -> void:
	var fixture: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://popover_focus_chrome.json"))
	for row in fixture.transitions:
		var panel := WevaDocument.new()
		panel.html = row.html
		panel.update_document(0)
		panel.set_focus("#outside")
		for i in row.actions.size():
			var action: Array = row.actions[i]
			if action[0] == "show": panel.show_popover("#" + str(action[1]))
			else: panel.hide_popover("#" + str(action[1]))
			panel.update_document(0)
			check(panel.get_focused_id() == row.states[i].settled, "Popover focus transition " + row.name + " step " + str(i))
		panel.free()
	for row in fixture.rows:
		var panel := WevaDocument.new()
		panel.html = row.html
		panel.update_document(0)
		panel.set_focus("#outside")
		panel.show_popover("#p")
		check(panel.get_focused_id() == row.shown, "Popover opening focus: " + row.kind + " " + row.tag)
		if row.moveOutside: panel.set_focus("#other")
		panel.hide_popover("#p")
		# Godot flushes state in the method; compare settled Chrome focus.
		# The oracle also retains immediate focus, whose timing can differ.
		check(panel.get_focused_id() == row.hiddenAfterFrame, "Popover settled closing focus: " + row.kind + " " + row.tag)
		panel.free()

func check_popover_chains() -> void:
	var fixture: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://popover_chains_chrome.json"))
	for row in fixture.rows:
		var panel := WevaDocument.new()
		panel.html = row.html
		panel.update_document(0)
		for i in row.actions.size():
			var action: Array = row.actions[i]
			var selector: String = "#" + str(action[1])
			if action[0] == "show": panel.show_popover(selector)
			elif action[0] == "hide": panel.hide_popover(selector)
			else:
				panel.update_document(0)
				panel.set_focus(selector)
				panel.send_key(KEY_ENTER)
			check(panel.query_all_ids(":popover-open") == PackedStringArray(row.states[i].open), "Popover chain " + row.name + " step " + str(i))
		panel.free()

func check_popover_siblings() -> void:
	# Chrome oracle: Tools/oracle/check_dialog_popover_chrome.cjs.
	for first in ["auto", "manual", "hint"]:
		for second in ["auto", "manual", "hint"]:
			var panel := WevaDocument.new()
			panel.html = '<div id=a popover=' + first + '>A</div><div id=b popover=' + second + '>B</div>'
			panel.update_document(0)
			check(panel.show_popover("#a"), "Open first " + first)
			check(panel.show_popover("#b"), "Open second " + second)
			var dismiss_first: bool = (second == "auto" and first != "manual") or (second == "hint" and first == "hint")
			var expected := PackedStringArray(["b"] if dismiss_first else ["a", "b"])
			check(panel.query_all_ids(":popover-open") == expected, "Popover sibling dismissal: " + first + " then " + second)
			panel.free()

func check_initial_focus() -> void:
	# Chrome oracle: Tools/oracle/check_dialog_focus_chrome.cjs.
	for use_modal in [false, true]:
		for attributes in ['tabindex=-1', 'tabindex=-1 autofocus', 'tabindex=0', 'tabindex=5', 'disabled autofocus', 'style="display:none" autofocus']:
			var panel := WevaDocument.new()
			panel.html = '<button id=outside>Open</button><dialog id=d><button id=first ' + attributes + '>First</button><button id=second>Second</button></dialog>'
			panel.update_document(0)
			panel.set_focus("#outside")
			if use_modal: panel.show_modal_dialog("#d")
			else: panel.show_dialog("#d")
			var expected := "second" if attributes.begins_with("disabled") or attributes.begins_with("style") else "first"
			check(panel.get_focused_id() == expected, "Dialog initial focus: " + attributes)
			panel.free()
		for tag in ["input", "textarea"]:
			var panel := WevaDocument.new()
			panel.html = '<button id=outside>Open</button><dialog id=d>' + ('<input id=field value=abcdef>' if tag == "input" else '<textarea id=field>abcdef</textarea>') + '</dialog>'
			panel.update_document(0)
			panel.set_focus("#outside")
			if use_modal: panel.show_modal_dialog("#d")
			else: panel.show_dialog("#d")
			check(panel.get_focused_id() == "field", "Dialog editing focus: " + tag)
			check(panel.get_element_selection("#field") == Vector2i.ZERO, "Dialog preserves initial selection: " + tag)
			panel.free()

func check_results() -> void:
	var cases: Array = [{"action": "close", "argument": "omit", "handler": "allow", "open": false, "value": "initial"}, {"action": "close", "argument": "omit", "handler": "set", "open": false, "value": "initial"}, {"action": "close", "argument": "omit", "handler": "prevent", "open": false, "value": "initial"}, {"action": "close", "argument": "omit", "handler": "close", "open": false, "value": "initial"}, {"action": "close", "argument": "empty", "handler": "allow", "open": false, "value": ""}, {"action": "close", "argument": "empty", "handler": "set", "open": false, "value": ""}, {"action": "close", "argument": "empty", "handler": "prevent", "open": false, "value": ""}, {"action": "close", "argument": "empty", "handler": "close", "open": false, "value": ""}, {"action": "close", "argument": "value", "handler": "allow", "open": false, "value": "accepted"}, {"action": "close", "argument": "value", "handler": "set", "open": false, "value": "accepted"}, {"action": "close", "argument": "value", "handler": "prevent", "open": false, "value": "accepted"}, {"action": "close", "argument": "value", "handler": "close", "open": false, "value": "accepted"}, {"action": "request", "argument": "omit", "handler": "allow", "open": false, "value": "initial"}, {"action": "request", "argument": "omit", "handler": "set", "open": false, "value": "handler"}, {"action": "request", "argument": "omit", "handler": "prevent", "open": true, "value": "initial"}, {"action": "request", "argument": "omit", "handler": "close", "open": false, "value": "handler-close"}, {"action": "request", "argument": "empty", "handler": "allow", "open": false, "value": ""}, {"action": "request", "argument": "empty", "handler": "set", "open": false, "value": ""}, {"action": "request", "argument": "empty", "handler": "prevent", "open": true, "value": "initial"}, {"action": "request", "argument": "empty", "handler": "close", "open": false, "value": "handler-close"}, {"action": "request", "argument": "value", "handler": "allow", "open": false, "value": "accepted"}, {"action": "request", "argument": "value", "handler": "set", "open": false, "value": "accepted"}, {"action": "request", "argument": "value", "handler": "prevent", "open": true, "value": "initial"}, {"action": "request", "argument": "value", "handler": "close", "open": false, "value": "handler-close"}]
	for row in cases:
		mode = "result-" + str(row.handler)
		closed_result = null
		doc = WevaDocument.new()
		doc.dialog_closed.connect(result_closed)
		doc.document_size = Vector2(640, 480)
		doc.controller = self
		doc.html = '<dialog id="d" on-cancel="cancelled">Hi</dialog>'
		add_child(doc)
		doc.update_document(0)
		check(doc.get_dialog_return_value("#d") == "", "Initial result empty")
		check(doc.set_dialog_return_value("#d", "initial"), "Set result property")
		doc.show_modal_dialog("#d")
		doc.update_document(0)
		var result: Variant = null if row.argument == "omit" else ("" if row.argument == "empty" else "accepted")
		if row.action == "close": check(doc.close_dialog("#d", result), "Close with optional result")
		else: check(doc.request_close_dialog("#d", result), "Request with optional result")
		doc.update_document(0)
		check(doc.has_element_attribute("#d", "open") == row.open, "Browser open state")
		check(doc.get_dialog_return_value("#d") == row.value, "Browser return value")
		check(closed_result == (null if row.open else row.value), "Close handler reads final result; veto emits no close")
		var long_result := "Réglages 🐎".repeat(100)
		check(doc.set_dialog_return_value("#d", long_result), "Set long Unicode result")
		check(doc.get_dialog_return_value("#d") == long_result, "Full Unicode result readback")
		doc.free()

func check_escape_input() -> void:
	var view := SubViewport.new()
	view.size = Vector2i(640, 480)
	add_child(view)
	for opening in ["modal", "markup", "attribute"]:
		for policy in ["none", "any", "closerequest", "invalid"]:
			for veto in [false, true]:
				mode = "prevent" if veto else "allow"
				cancels = 0
				doc = WevaDocument.new()
				doc.document_size = Vector2(640, 480)
				doc.controller = self
				doc.html = '<dialog id="d" on-cancel="cancelled" closedby="%s" %s><input id="field"></dialog>' % [policy, "open" if opening == "markup" else ""]
				view.add_child(doc)
				doc.update_document(0)
				if opening == "modal": doc.show_modal_dialog("#d")
				elif opening == "attribute": doc.set_element_attribute("#d", "open", "")
				doc.set_dialog_return_value("#d", "initial")
				doc.update_document(0)
				doc.set_focus("#field")
				doc.grab_focus()
				await get_tree().process_frame
				for pressed in [true, false]:
					var key := InputEventKey.new()
					key.keycode = KEY_ESCAPE
					key.pressed = pressed
					view.push_input(key, true)
				doc.update_document(0)
				var requested: bool = policy in ["any", "closerequest"] or (opening == "modal" and policy == "invalid")
				check(cancels == int(requested), "Escape cancellation through native input")
				check(doc.has_element_attribute("#d", "open") == (not requested or veto), "Escape final open state")
				check(doc.get_dialog_return_value("#d") == ("" if requested and not veto else "initial"), "Escape result or veto preservation")
				doc.free()
	mode = "allow"
	cancels = 0
	doc = WevaDocument.new()
	doc.document_size = Vector2(640, 480)
	doc.controller = self
	doc.html = '<dialog id="d" on-cancel="cancelled"><select id="choice"><option>A</option><option>B</option></select><div id="popup" popover="auto">Help</div></dialog>'
	view.add_child(doc)
	doc.update_document(0)
	check(doc.show_modal_dialog("#d"), "Open priority modal")
	doc.update_document(0)
	doc.set_focus("#choice")
	check(doc.open_select("#choice"), "Open priority dropdown")
	check(doc.show_popover("#popup"), "Open priority popover")
	doc.update_document(0)
	doc.grab_focus()
	await get_tree().process_frame
	check(doc.get_open_select() == "choice", "Dropdown remains open under popover")
	check(doc.has_element_attribute("#popup", "data-popover-open"), "Popover starts open")
	for step in range(3):
		for pressed in [true, false]:
			var key := InputEventKey.new()
			key.keycode = KEY_ESCAPE
			key.pressed = pressed
			view.push_input(key, true)
		doc.update_document(0)
		check(not doc.has_element_attribute("#popup", "data-popover-open"), "Escape dismisses popover first")
		check(doc.get_open_select() == ("choice" if step == 0 else ""), "Escape dismisses dropdown second")
		check(doc.has_element_attribute("#d", "open") == (step < 2), "Escape dismisses modal last")
		check(cancels == (1 if step == 2 else 0), "Only modal dismissal requests cancellation")
	doc.free()
	view.free()

func _ready() -> void:
	for is_modal in [false, true]:
		modal = is_modal
		for action in ["allow", "prevent", "update-prevent", "signal-prevent", "close", "reopen", "reload", "remove"]:
			mode = action
			cancels = 0
			closes = 0
			signal_cancels = 0
			doc = WevaDocument.new()
			doc.document_size = Vector2(640, 480)
			doc.paused = true
			doc.controller = self
			doc.html = '<button id="outside">Open</button><dialog id="d" on-cancel="cancelled"><input id="field" value="abc"></dialog>'
			add_child(doc)
			doc.update_document(0)
			doc.dialog_cancel_requested.connect(cancel_signal)
			doc.dialog_closed.connect(closed)
			check(not doc.prevent_default(), "No event to cancel initially")
			check(doc.request_close_dialog("#d"), "Closed request is a no-op")
			check(cancels == 0 and closes == 0, "Closed request emits nothing")
			if modal: doc.show_modal_dialog("#d")
			else: doc.show_dialog("#d")
			doc.update_document(0)
			check(doc.request_close_dialog("#d"), "Request succeeds")
			check(cancels == 1 and signal_cancels == 1, "Handlers run once before request returns")
			check(closes == (1 if action in ["allow", "close", "reopen"] else 0), "Expected close count for " + action)
			if action != "remove":
				check(doc.has_element_attribute("#d", "open") == (action in ["prevent", "update-prevent", "signal-prevent", "reopen", "reload"]), "Expected final open state for " + action)
			check(not doc.prevent_default(), "Cancel context ends after event drain")
			doc.free()
	check_popover_outside()
	check_popover_focus()
	check_popover_chains()
	check_popover_siblings()
	check_initial_focus()
	check_results()
	await check_escape_input()
	print("godot dialog cancellation: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
