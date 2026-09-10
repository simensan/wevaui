extends Node

var checks := 0
var failures := 0
var doc: WevaDocument
var action := ""
var calls := 0
var signals := 0
var route := "show"
var closing_order: Array[String] = []
var pointer_events: Array = []
var pointer_cancel := false

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", action, ": ", message)

func opening(id: String) -> void:
	calls += 1
	check(id == "target", "handler target")
	check(not doc.has_element_attribute("#target", "data-popover-open"), "handler precedes opening")
	check(doc.has_element_attribute("#other", "data-popover-open"), "handler precedes sibling dismissal")
	check(doc.get_focused_id() == "outside", "handler precedes focus change")
	if action == "cancel":
		check(doc.prevent_default(), "opening can be cancelled")
	elif action == "update-cancel":
		doc.update_document(0)
		doc.query_bounds("#target")
		check(not doc.has_element_attribute("#target", "data-popover-open"), "nested update preserves pending opening")
		check(doc.prevent_default(), "nested update preserves cancellation context")
	elif action == "remove":
		doc.remove_element("#target")
	elif action == "reload":
		doc.html = '<div id="target" popover>Replacement</div>'
		doc.update_document(0)
	elif action == "reentrant":
		check(doc.show_popover("#target"), "duplicate opening accepted without recursion")

func opening_signal(id: String, open: bool) -> void:
	if id != "target": return
	signals += 1
	check(open, "opening signal state")
	if action == "signal-cancel":
		check(doc.prevent_default(), "signal can veto opening")

func closing(id: String) -> void:
	calls += 1
	check(id == "target", "closing handler target")
	check(doc.has_element_attribute("#target", "data-popover-open"), "closing handler sees open menu")
	check(not doc.prevent_default(), "closing cannot be cancelled")
	if action == "update":
		doc.update_document(0)
		check(doc.has_element_attribute("#target", "data-popover-open"), "nested update does not close early")
	elif action == "remove": doc.remove_element("#target")
	elif action == "reload":
		doc.html = '<div id="target" popover>Replacement</div>'
		doc.update_document(0)
	elif action == "reentrant":
		check(doc.hide_popover("#target"), "duplicate hide accepted")
		check(doc.show_popover("#target"), "show while still open is a no-op")

func check_closing() -> void:
	for close_route in ["hide", "toggle", "button", "escape", "outside"]:
		for kind in ["auto", "hint", "manual"]:
			if kind == "manual" and close_route in ["escape", "outside"]: continue
			for next_action in ["allow", "update", "remove", "reload", "reentrant"]:
				action = next_action
				calls = 0
				doc = WevaDocument.new()
				doc.controller = self
				doc.document_size = Vector2(640, 480)
				doc.css = "[popover]{position:fixed;left:200px;top:100px;width:100px;height:60px;margin:0;inset-inline-end:auto;inset-block-end:auto}"
				doc.html = '<button id="outside" popovertarget="target">Outside</button><div id="target" popover="%s"><input id="inside" autofocus></div>' % kind
				add_child(doc)
				doc.set_process(false)
				doc.update_document(0)
				doc.show_popover("#target")
				doc.set_element_attribute("#target", "on-beforetoggle", "closing")
				if close_route == "hide": check(doc.hide_popover("#target"), "hide accepted")
				elif close_route == "toggle": check(doc.toggle_popover("#target"), "toggle close accepted")
				elif close_route == "escape": doc.send_key(KEY_ESCAPE)
				elif close_route == "outside":
					doc.set_pointer(Vector2(20, 350), 0)
					doc.set_pointer(Vector2(20, 350), 1)
					doc.set_pointer(Vector2(20, 350), 0)
				else:
					doc.set_focus("#outside")
					doc.send_key(KEY_ENTER)
				check(calls == 1, "closing handler runs once before return")
				check(not doc.has_element_attribute("#target", "data-popover-open"), "close completes")
				check(not doc.prevent_default(), "no cancellation after close")
				doc.free()

func record_closing(id: String) -> void:
	closing_order.append(id)
	if id == "child" and action == "change-replacement-mode": doc.set_element_attribute("#replacement", "popover", "manual")
	if id == "child" and action == "remove-replacement": doc.remove_element("#replacement")
	if action == "attribute-remove": check(not doc.has_element_attribute("#parent", "popover"), "handler sees removed attribute")
	if action == "attribute-manual": check(doc.get_element_attribute("#parent", "popover") == "manual", "handler sees changed mode")
	if id == "replacement":
		check(not doc.has_element_attribute("#replacement", "data-popover-open"), "replacement handler precedes opening")
		if action == "cancel-replacement": check(doc.prevent_default(), "replacement veto accepted")
	else:
		check(doc.has_element_attribute("#" + id, "data-popover-open"), "ordered closing handler sees open menu")
		check(not doc.has_element_attribute("#replacement", "data-popover-open"), "closing precedes replacement opening")
		check(not doc.prevent_default(), "ordered closing is non-cancellable")

func check_parent_closing() -> void:
	for kind in ["auto", "hint", "manual"]:
		doc = WevaDocument.new()
		doc.controller = self
		doc.html = '<div id="parent" popover="%s"><div id="child" popover="%s">Child</div></div>' % [kind, kind]
		doc.update_document(0)
		doc.show_popover("#parent")
		doc.show_popover("#child")
		for id in ["parent", "child"]: doc.set_element_attribute("#" + id, "on-beforetoggle", "record_closing")
		closing_order.clear()
		check(doc.hide_popover("#parent"), "parent close accepted")
		check(closing_order == (["parent"] if kind == "manual" else ["child", "parent"]), "child closing precedes parent event")
		check(not doc.has_element_attribute("#parent", "data-popover-open"), "parent closed")
		check(doc.has_element_attribute("#child", "data-popover-open") == (kind == "manual"), "manual child remains independent")
		doc.free()

func check_replacement() -> void:
	for kind in ["auto", "hint", "manual"]:
		for mutation in ["allow-replacement", "cancel-replacement", "change-replacement-mode", "remove-replacement"]:
			action = mutation
			var cancel: bool = mutation == "cancel-replacement"
			doc = WevaDocument.new()
			doc.controller = self
			doc.html = '<div id="parent" popover="%s"><div id="child" popover="%s">Child</div></div><div id="replacement" popover="%s">Replacement</div>' % [kind, kind, kind]
			doc.update_document(0)
			doc.show_popover("#parent")
			doc.show_popover("#child")
			for id in ["parent", "child", "replacement"]: doc.set_element_attribute("#" + id, "on-beforetoggle", "record_closing")
			closing_order.clear()
			check(doc.show_popover("#replacement"), "replacement request accepted")
			check(closing_order == (["replacement"] if cancel or kind == "manual" else ["replacement", "child", "parent"]), "replacement event ordering")
			check(doc.has_element_attribute("#replacement", "data-popover-open") == (not cancel and (kind == "manual" or mutation == "allow-replacement")), "replacement final state")
			for id in ["parent", "child"]: check(doc.has_element_attribute("#" + id, "data-popover-open") == (cancel or kind == "manual"), "existing stack final state")
			doc.free()

func check_attribute_closing() -> void:
	for kind in ["auto", "hint", "manual"]:
		for mutation in ["remove", "manual", "same"]:
			action = "attribute-" + mutation
			doc = WevaDocument.new()
			doc.controller = self
			doc.html = '<div id="parent" popover="%s"><div id="child" popover="%s">Child</div></div>' % [kind, kind]
			doc.update_document(0)
			doc.show_popover("#parent")
			doc.show_popover("#child")
			for id in ["parent", "child"]: doc.set_element_attribute("#" + id, "on-beforetoggle", "record_closing")
			closing_order.clear()
			if mutation == "remove": check(doc.remove_element_attribute("#parent", "popover"), "attribute removal accepted")
			else: check(doc.set_element_attribute("#parent", "popover", kind if mutation == "same" else "manual"), "attribute change accepted")
			var unchanged: bool = mutation == "same" or (mutation == "manual" and kind == "manual")
			var expected: Array = [] if unchanged else (["parent"] if kind == "manual" else ["child", "parent"])
			check(closing_order == expected, "attribute closing event order")
			check(doc.has_element_attribute("#parent", "data-popover-open") == unchanged, "attribute parent final state")
			check(doc.has_element_attribute("#child", "data-popover-open") == (unchanged or kind == "manual"), "attribute child final state")
			doc.free()

func pointer_before(id: String, open: bool) -> void:
	pointer_events.append({"id": id, "newState": "open" if open else "closed", "open": Array(doc.query_all_ids(":popover-open")), "focus": doc.get_focused_id()})
	if id == "target" and open and pointer_cancel: check(doc.prevent_default(), "pointer opening veto")

func check_pointer_opening() -> void:
	var fixture: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://popover_pointer_chrome.json"))
	for row in fixture.rows:
		doc = WevaDocument.new()
		doc.document_size = Vector2(640, 480)
		doc.html = '<button id="outside" popovertarget="target">Open</button><div id="other" popover="%s">Other</div><div id="target" popover="%s"><input id="inside" autofocus></div>' % [row.mode, row.mode]
		doc.css = 'body{margin:0}#outside{position:absolute;left:10px;top:10px;width:100px;height:30px}[popover]{position:fixed;margin:0;inset:auto;left:200px;top:100px;width:100px;height:60px}'
		doc.update_document(0)
		doc.set_focus("#outside")
		doc.show_popover("#other")
		pointer_events.clear()
		pointer_cancel = row.cancel
		doc.element_before_toggled.connect(pointer_before)
		doc.set_pointer(Vector2(30, 20), 0)
		doc.set_pointer(Vector2(30, 20), 1)
		doc.set_pointer(Vector2(30, 20), 0)
		check(pointer_events == row.events, "pointer event snapshots match Chrome " + row.mode + str(row.cancel))
		check(Array(doc.query_all_ids(":popover-open")) == row.final.open, "pointer final menus match Chrome")
		check(doc.get_focused_id() == row.final.focus, "pointer final focus matches Chrome")
		doc.free()

func _ready() -> void:
	for next_route in ["show", "toggle", "button"]:
		route = next_route
		for kind in ["auto", "hint", "manual"]:
			for next_action in ["allow", "cancel", "signal-cancel", "update-cancel", "remove", "reload", "reentrant"]:
				action = next_action
				calls = 0
				signals = 0
				doc = WevaDocument.new()
				doc.controller = self
				doc.html = '<button id="outside" popovertarget="target">Outside</button><div id="other" popover="%s">Existing</div><div id="target" popover="%s" on-beforetoggle="opening"><input id="inside" autofocus></div>' % [kind, kind]
				add_child(doc)
				doc.set_process(false)
				doc.update_document(0)
				doc.set_focus("#outside")
				doc.show_popover("#other")
				doc.element_before_toggled.connect(opening_signal)
				if route == "show": check(doc.show_popover("#target"), "opening request accepted")
				elif route == "toggle": check(doc.toggle_popover("#target"), "toggle opening accepted")
				else: doc.send_key(KEY_ENTER)
				check(calls == 1 and signals == 1, "handler and signal run once before return")
				var opened := action in ["allow", "reentrant"]
				check(doc.has_element_attribute("#target", "data-popover-open") == opened, "final target state")
				if action != "reload":
					check(doc.has_element_attribute("#other", "data-popover-open") == (not opened or kind == "manual"), "final sibling state")
					check(doc.get_focused_id() == ("inside" if opened else "outside"), "final focus")
				check(not doc.prevent_default(), "cancellation context ends after return")
				doc.free()
	check_closing()
	check_parent_closing()
	check_replacement()
	check_attribute_closing()
	check_pointer_opening()
	print("godot popover beforetoggle: %d checks, %d failures" % [checks, failures])
	get_tree().quit(0 if failures == 0 else 1)
