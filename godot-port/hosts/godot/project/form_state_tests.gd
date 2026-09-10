extends Node

var checks := 0
var failures := 0
var viewport: SubViewport
var signals: Array[String] = []
var reset_data: Dictionary = {}

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func key(code: Key) -> void:
	for pressed in [true, false]:
		var event := InputEventKey.new()
		event.keycode = code
		event.pressed = pressed
		viewport.push_input(event, true)

func text(value: String) -> void:
	for character in value:
		var event := InputEventKey.new()
		event.unicode = character.unicode_at(0)
		event.pressed = true
		viewport.push_input(event, true)

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(640, 480)
	add_child(viewport)
	var select_widths = JSON.parse_string(FileAccess.get_file_as_string("res://select_width_chrome.json"))
	for row in select_widths.rows:
		var compact := WevaDocument.new()
		compact.document_size = Vector2(640, 480)
		compact.css = "select{" + row.css + "}"
		compact.html = '<div style="width:320px"><select id="s" ' + ("multiple size=3" if row.multiple else "") + '><option>Low</option><option>Medium</option></select></div>'
		viewport.add_child(compact)
		compact.update_document(0)
		check(is_equal_approx(compact.query_bounds("#s").size.x, float(row.width)), "Select width matches Chrome: " + row.css + " multiple=" + str(row.multiple))
		compact.set_element_attribute("#s", "style", "width:70px;min-width:0;max-width:none")
		compact.update_document(0)
		check(is_equal_approx(compact.query_bounds("#s").size.x, 70.0), "Select width updates at runtime")
		compact.free()
	var doc := WevaDocument.new()
	doc.document_size = Vector2(640, 480)
	doc.css = "html,body{margin:0}input,textarea,select{display:block;width:150px;height:34px}textarea{height:60px}"
	doc.html = '<form id="settings" on-reset="restored"><input id="name" value="Ada" data-model="Name"><textarea id="bio" data-model="Bio">Default bio</textarea><input id="sound" type="checkbox" checked data-model="Sound"><select id="quality" data-model="Quality"><option value="low">Low</option><option value="high" selected>High</option></select><button id="restore" type="reset">Restore</button><input id="input_reset" type="reset" value="Restore input"></form><input id="external" form="settings" value="outside" data-model="Outside"><input id="unowned" data-model="Unowned"><p id="label">{{ Name }} {{ Bio }} {{ Quality }}</p>'
	doc.data = {"Name":"Grace", "Bio":"Edited bio", "Sound":false, "Quality":"low", "Outside":"changed", "Unowned":"keep"}
	viewport.add_child(doc)
	await get_tree().process_frame
	doc.value_changed.connect(func(_id, _text): signals.append("input"))
	doc.value_committed.connect(func(_id, _text): signals.append("change"))
	doc.form_reset.connect(func(id): signals.append("reset:" + id); reset_data = doc.data.duplicate(true))
	doc.handler_invoked.connect(func(handler, _id): signals.append("handler:" + handler))
	check(doc.get_element_value("#name") == "Grace", "Data initializes the live value")
	check(doc.get_element_text("#bio") == "Default bio", "Textarea markup remains its reset default")
	check(doc.has_element("#name[value=Ada]"), "Attribute selectors retain input defaults")
	check(doc.has_element("#sound[checked]") and not doc.has_element("#sound:checked"), "Checkedness and the checked attribute differ")
	check(doc.get_element_value("#quality") == "low", "Select data-model sets live selectedness")
	check(doc.has_element('#quality option[value=high][selected]'), "Select model preserves selected defaults")
	doc.set_focus("#name")
	doc.paste_text("!")
	check(doc.get_element_value("#name") == "Grace!", "Typing edits the model value")
	signals.clear()
	check(doc.reset_form("#settings"), "Script reset succeeds")
	check(signals == ["handler:restored", "reset:settings"], "Reset reports its handler and signal without input/change")
	check(doc.get_element_value("#name") == "Ada", "Reset restores input default")
	check(doc.get_element_value("#bio") == "Default bio", "Reset restores textarea default")
	check(doc.get_element_value("#sound") == "on", "Reset restores checked default")
	check(doc.get_element_value("#quality") == "high", "Reset restores selected default")
	check(doc.get_element_value("#external") == "outside", "Reset reaches an external form owner")
	check(doc.get_element_value("#unowned") == "keep", "Reset respects form ownership")
	check(reset_data == {"Name":"Ada", "Bio":"Default bio", "Sound":true, "Quality":"high", "Outside":"outside", "Unowned":"keep"}, "Bound data is restored before form_reset")
	check(doc.get_element_text("#label") == "Ada Default bio high", "Bindings refresh as one reset operation")
	check(doc.get_focused_id() == "name", "Script reset preserves focus")
	check(not doc.undo(), "Reset clears the owned undo history")
	doc.set_element_attribute("#name", "value", "New default")
	check(doc.get_element_value("#name") == "New default", "Reset cleared the dirty value flag")
	doc.set_element_value("#name", "game edit")
	doc.set_element_attribute("#name", "value", "Latest default")
	check(doc.get_element_value("#name") == "game edit", "Dirty values ignore default changes")
	doc.set_focus("#restore")
	signals.clear()
	key(KEY_ENTER)
	check(doc.get_element_value("#name") == "Latest default", "Native Enter activates a reset button")
	check(signals == ["handler:restored", "reset:settings"], "Native reset emits no spurious value commit")
	doc.set_element_value("#name", "changed again")
	doc.set_focus("#input_reset")
	signals.clear()
	key(KEY_SPACE)
	check(doc.get_element_value("#name") == "Latest default", "Native Space activates input type=reset")
	check(signals == ["handler:restored", "reset:settings"], "Input reset shares the event contract")
	doc.set_focus("#bio")
	doc.set_composition("日本", 2, 2)
	doc.set_element_text("#bio", "New bio")
	check(doc.get_element_value("#bio").ends_with("日本"), "Default text changes preserve active IME")
	signals.clear()
	doc.reset_form("#settings")
	check(doc.get_element_value("#bio") == "New bio", "Reset discards preedit and restores the current default")
	check(signals == ["handler:restored", "reset:settings"], "IME reset creates no input/change signal")
	check(not doc.undo(), "Preedit does not survive in reset undo history")
	check(not doc.reset_form("#missing") and not doc.reset_form("#name"), "Reset rejects missing elements and non-forms")
	doc.free()
	var group := WevaDocument.new()
	group.document_size = Vector2(640, 480)
	group.css = "input:enabled{width:73px}input:disabled{width:37px}"
	group.html = '<fieldset id="group" disabled><legend><input id="master" type="checkbox"></legend><input id="blocked"><legend><input id="second"></legend></fieldset>'
	viewport.add_child(group)
	await get_tree().process_frame
	group.set_focus("#master")
	check(group.get_focused_id() == "master", "First legend master control remains focusable")
	key(KEY_SPACE)
	check(group.get_element_value("#master") == "on", "Native Space activates a master control in a disabled fieldset")
	check(group.get_computed_style("#blocked", "width") == "37px", "Disabled fieldset styles its controls")
	group.set_focus("")
	group.set_focus("#second")
	check(group.get_focused_id() != "second", "Second legend has no disabledness exemption")
	group.remove_element_attribute("#group", "disabled")
	check(group.get_computed_style("#blocked", "width") == "73px", "Live fieldset enablement refreshes child CSS")
	group.set_focus("#blocked")
	check(group.get_focused_id() == "blocked", "Enabled group allows control focus")
	group.set_element_attribute("#group", "disabled", "")
	group.update_document(0)
	check(group.get_focused_id() == "", "Disabling a group releases its focused control")
	group.free()
	var disabled := WevaDocument.new()
	disabled.document_size = Vector2(640, 480)
	disabled.css = "html,body{margin:0}button{width:100px;height:60px}button:hover{width:120px}button:active{width:140px}"
	disabled.html = '<button id="off" disabled title="Requires a workbench">Craft</button>'
	viewport.add_child(disabled)
	await get_tree().process_frame
	var clicks: Array[String] = []
	disabled.element_clicked.connect(func(id): clicks.append(id))
	viewport.notify_mouse_entered()
	var motion := InputEventMouseMotion.new()
	motion.position = Vector2(50,30)
	motion.global_position = motion.position
	viewport.push_input(motion, true)
	check(disabled.get_computed_style("#off", "width") == "120px", "Disabled actions retain native hover styling")
	disabled.update_document(.7)
	check(disabled.get_element_text(".ui-tooltip") == "Requires a workbench", "Disabled actions explain availability through title tooltips")
	for pressed in [true, false]:
		var pointer := InputEventMouseButton.new()
		pointer.position = Vector2(50,30)
		pointer.global_position = pointer.position
		pointer.button_index = MOUSE_BUTTON_LEFT
		pointer.pressed = pressed
		viewport.push_input(pointer, true)
		if pressed:
			check(disabled.get_computed_style("#off", "width") == "140px", "Disabled actions retain pressed styling")
	check(clicks.is_empty(), "Disabled actions never emit click activation")
	check(disabled.get_focused_id() == "", "Disabled actions cannot take focus")
	disabled.free()
	var required := WevaDocument.new()
	required.document_size = Vector2(640,480)
	required.css = "input:required{width:37px}input:optional{width:73px}"
	required.html = '<input id="field" required="{{ Needed }}"><input id="range" type="range" required>'
	required.data = {"Needed":true}
	viewport.add_child(required)
	await get_tree().process_frame
	check(required.get_computed_style("#field", "width") == "37px", "Required binding applies required CSS")
	required.data = {"Needed":false}
	check(required.get_computed_style("#field", "width") == "73px", "Optional CSS follows binding changes")
	required.set_element_attribute("#field", "required", "false")
	check(required.get_computed_style("#field", "width") == "37px", "Required is a boolean attribute regardless of its text")
	check(required.get_computed_style("#range", "width") == "73px", "Range input remains optional despite required attribute")
	required.free()
	var locked := WevaDocument.new()
	locked.document_size = Vector2(640,480)
	locked.css = "input:read-write,textarea:read-write{width:73px}input:read-only,textarea:read-only{width:37px}"
	locked.html = '<fieldset id="group"><legend><input id="master"></legend><input id="field" value="seed" readonly="{{ Locked }}"><textarea id="area">note</textarea></fieldset>'
	locked.data = {"Locked":true}
	viewport.add_child(locked)
	await get_tree().process_frame
	check(locked.get_computed_style("#field", "width") == "37px", "Read-only binding styles locked text")
	locked.set_focus("#field")
	check(locked.get_focused_id() == "field", "Read-only text stays focusable for selection and copying")
	text("x")
	check(locked.get_element_value("#field") == "seed", "Read-only styling agrees with blocked typing")
	locked.data = {"Locked":false}
	check(locked.get_computed_style("#field", "width") == "73px", "Unlocking updates read-write styling")
	text("x")
	check(locked.get_element_value("#field").contains("x"), "Unlocked text accepts native typing")
	locked.set_element_attribute("#group", "disabled", "")
	check(locked.get_computed_style("#area", "width") == "37px", "Disabled fieldset makes textarea read-only")
	check(locked.get_computed_style("#master", "width") == "73px", "First legend keeps read-write styling")
	locked.remove_element_attribute("#group", "disabled")
	locked.set_element_attribute("#field", "type", "checkbox")
	check(locked.get_computed_style("#field", "width") == "37px", "Live type change updates read-only selector")
	locked.free()
	var defaults := WevaDocument.new()
	defaults.document_size = Vector2(640,480)
	defaults.css = "button:default span{width:37px}button:not(:default) span{width:73px}"
	defaults.html = '<input id="external" type="submit" form="settings" disabled><form id="settings"><button id="apply"><span id="label">Apply</span></button><input id="backup" type="submit"><input id="check" type="checkbox" checked></form>'
	viewport.add_child(defaults)
	await get_tree().process_frame
	check(defaults.has_element("#external:default"), "Disabled external submit remains the form default")
	check(defaults.get_computed_style("#label", "width") == "73px", "Later action starts without default styling")
	defaults.set_element_attribute("#external", "form", "missing")
	check(defaults.get_computed_style("#label", "width") == "37px", "Changing a remote form owner updates default descendant CSS")
	defaults.set_element_attribute("#external", "form", "settings")
	check(defaults.get_computed_style("#label", "width") == "73px", "Restoring the remote submit refreshes cached default styles")
	defaults.remove_element("#external")
	check(defaults.get_computed_style("#label", "width") == "37px", "Removing the default promotes the next submit")
	defaults.set_element_attribute("#apply", "type", "button")
	check(defaults.has_element("#backup:default"), "Changing submit type promotes the backup action")
	defaults.set_element_value("#check", "false")
	check(defaults.has_element("#check:default"), "Live checkbox changes preserve its markup default")
	defaults.free()
	var submit := WevaDocument.new()
	submit.document_size = Vector2(640,480)
	submit.css = "html,body{margin:0}input,button{display:block;width:120px;height:40px}"
	submit.html = '<form id="settings"><button id="command" command="--inspect">Inspect</button><input id="field"><button id="apply">Apply</button></form>'
	viewport.add_child(submit)
	await get_tree().process_frame
	var submitted: Array[String] = []
	var activated: Array[String] = []
	submit.form_submitted.connect(func(id): submitted.append(id))
	submit.element_clicked.connect(func(id): activated.append(id))
	submit.set_pointer(Vector2(30,20),1)
	submit.set_pointer(Vector2(30,20),0)
	check(activated == ["command"] and submitted.is_empty(), "Command click does not submit settings")
	submit.set_focus("#command")
	key(KEY_ENTER)
	check(submitted.is_empty(), "Enter on command does not submit settings")
	activated.clear()
	submit.set_focus("#field")
	key(KEY_ENTER)
	check(activated == ["apply"] and submitted == ["settings"], "Implicit Enter chooses the actual submit action")
	submitted.clear()
	activated.clear()
	submit.set_element_attribute("#apply", "disabled", "")
	key(KEY_ENTER)
	check(activated.is_empty() and submitted.is_empty(), "Disabled default blocks implicit submission")
	submit.set_element_attribute("#command", "type", "SUBMIT")
	submit.set_focus("#command")
	key(KEY_ENTER)
	check(submitted == ["settings"], "Explicit submit type still submits when command is present")
	submit.free()
	var top := WevaDocument.new()
	top.document_size = Vector2(640,480)
	top.css = "dialog:modal span{width:37px}dialog:not(:modal) span{width:73px}:popover-open span{width:41px}[popover]:not(:popover-open) span{width:79px}"
	top.html = '<dialog id="dialog" data-modal><span id="label">Confirm</span></dialog><div id="menu" popover data-popover-open><span id="item">Menu</span></div>'
	viewport.add_child(top)
	await get_tree().process_frame
	check(not top.has_element("#dialog:modal") and not top.has_element("#menu:popover-open"), "Authored data attributes cannot create top-layer state")
	top.show_dialog("#dialog")
	check(top.get_computed_style("#label", "width") == "73px", "Nonmodal dialog does not match modal styling")
	top.close_dialog("#dialog")
	top.show_modal_dialog("#dialog")
	check(top.get_computed_style("#label", "width") == "37px", "Showing a modal restyles descendants")
	top.remove_element_attribute("#dialog", "data-modal")
	check(top.has_element("#dialog:modal"), "Removing a data marker does not clear actual modality")
	top.close_dialog("#dialog")
	check(not top.has_element("#dialog:modal"), "Closing a modal clears its selector state")
	top.show_popover("#menu")
	check(top.get_computed_style("#item", "width") == "41px", "Showing a popover restyles descendants")
	top.set_element_attribute("#menu", "popover", "AUTO")
	check(top.has_element("#menu:popover-open"), "Equivalent popover keywords preserve visibility")
	top.grab_focus()
	key(KEY_ESCAPE)
	check(top.get_computed_style("#item", "width") == "79px", "Escape refreshes closed-popover CSS")
	top.show_popover("#menu")
	top.set_pointer(Vector2(600,450),1)
	top.set_pointer(Vector2(600,450),0)
	check(not top.has_element("#menu:popover-open"), "Outside clicks clear popover selector state")
	top.show_popover("#menu")
	top.remove_element_attribute("#menu", "popover")
	top.set_element_attribute("#menu", "popover", "")
	check(not top.has_element("#menu:popover-open"), "Removing and restoring popover does not reopen it")
	top.free()
	var notify := WevaDocument.new()
	notify.document_size = Vector2(640,480)
	notify.html = '<div id="popup" popover="{{ Mode }}" on-toggle="changed">Menu</div>'
	notify.data = {"Mode":"auto"}
	viewport.add_child(notify)
	await get_tree().process_frame
	var toggles: Array[bool] = []
	var handlers: Array[String] = []
	notify.element_toggled.connect(func(_id, opened): toggles.append(opened))
	notify.handler_invoked.connect(func(handler, _id): handlers.append(handler))
	notify.show_popover("#popup")
	notify.update_document(0)
	check(toggles == [true], "Opening a popover reports opened=true")
	toggles.clear()
	handlers.clear()
	notify.data = {"Mode":"manual"}
	notify.update_document(0)
	check(toggles == [false] and handlers == ["changed"], "Changing a bound popover mode notifies close exactly once")
	notify.show_popover("#popup")
	notify.update_document(0)
	toggles.clear()
	notify.hide_popover("#popup")
	notify.hide_popover("#popup")
	notify.update_document(0)
	check(toggles == [false], "Repeated explicit hide emits one closed notification")
	notify.show_popover("#popup")
	notify.update_document(0)
	toggles.clear()
	notify.remove_element_attribute("#popup", "popover")
	notify.update_document(0)
	check(toggles == [false], "Removing the popover attribute notifies close")
	notify.set_element_attribute("#popup", "popover", "auto")
	notify.data = {"Mode":"auto"}
	notify.show_popover("#popup")
	notify.update_document(0)
	toggles.clear()
	notify.data = {"Mode":"AUTO"}
	notify.update_document(0)
	check(toggles.is_empty() and notify.has_element("#popup:popover-open"), "Equivalent mode changes do not send a spurious toggle")
	notify.hide_popover("#popup")
	notify.update_document(0)
	toggles.clear()
	notify.handler_invoked.connect(func(_handler, _id):
		if notify.has_element("#popup:popover-open"):
			notify.hide_popover("#popup"))
	notify.show_popover("#popup")
	notify.update_document(0)
	check(toggles == [true,false], "Reentrant close preserves the queued opened state before closed")
	notify.free()
	var api := WevaDocument.new()
	api.document_size = Vector2(640,480)
	api.html = '<dialog id="dialog" popover>Confirm</dialog><div id="menu" popover>Menu</div><div id="manual" popover="manual">Pinned</div>'
	viewport.add_child(api)
	await get_tree().process_frame
	var dialog_states: Array[bool] = []
	api.element_toggled.connect(func(id, opened):
		if id == "dialog": dialog_states.append(opened))
	check(api.show_dialog("#dialog"), "Nonmodal show succeeds from closed state")
	api.update_document(0)
	check(dialog_states == [true] and api.query_bounds("#dialog").size.y > 0, "Dialog opening emits true and creates visible content")
	dialog_states.clear()
	check(not api.show_modal_dialog("#dialog") and not api.has_element("#dialog:modal"), "An open nonmodal dialog rejects modal show without mutation")
	api.show_dialog("#dialog")
	api.update_document(0)
	check(dialog_states.is_empty(), "Repeated same-mode show does not notify again")
	api.close_dialog("#dialog")
	api.show_popover("#menu")
	api.show_popover("#manual")
	api.show_modal_dialog("#dialog")
	api.update_document(0)
	check(not api.has_element("#menu:popover-open") and api.has_element("#manual:popover-open"), "Modal opening dismisses unrelated auto popovers but preserves manual ones")
	check(not api.show_dialog("#dialog") and api.has_element("#dialog:modal"), "An open modal dialog rejects nonmodal show")
	check(not api.show_popover("#dialog") and not api.toggle_popover("#dialog"), "A modal dialog cannot be opened as a popover")
	api.close_dialog("#dialog")
	api.show_popover("#dialog")
	check(not api.show_modal_dialog("#dialog") and api.has_element("#dialog:popover-open"), "Modal show rejects a visible popover without closing it")
	api.hide_popover("#dialog")
	check(api.show_modal_dialog("#dialog"), "Modal opening succeeds after explicitly hiding the popover")
	api.free()
	var modal := WevaDocument.new()
	modal.document_size = Vector2(640,480)
	modal.css = "html,body{margin:0}#behind{position:absolute;left:10px;top:10px;width:100px;height:40px}dialog{position:fixed;left:200px;top:100px;width:200px;height:100px;margin:0;padding:0}"
	modal.html = '<button id="behind">Inventory</button><dialog id="modal"><button id="inside">Confirm</button></dialog><dialog id="second"><button id="other" autofocus>Apply</button></dialog>'
	viewport.add_child(modal)
	await get_tree().process_frame
	modal.grab_focus()
	modal.set_focus("#behind")
	modal.show_modal_dialog("#modal")
	modal.update_document(0)
	check(modal.get_focused_id() == "inside", "Opening a modal focuses its first control")
	modal.set_focus("#behind")
	check(modal.get_focused_id() == "inside", "A modal blocks explicit focus outside its subtree")
	var modal_clicks: Array[String] = []
	modal.element_clicked.connect(func(id): modal_clicks.append(id))
	for pressed in [true,false]:
		var pointer := InputEventMouseButton.new()
		pointer.position = Vector2(30,30)
		pointer.global_position = pointer.position
		pointer.button_index = MOUSE_BUTTON_LEFT
		pointer.pressed = pressed
		viewport.push_input(pointer,true)
	check(modal_clicks == ["modal"], "Backdrop clicks target the dialog, never the inventory underneath")
	modal.set_focus("#inside")
	key(KEY_TAB)
	check(modal.get_focused_id() != "behind", "Tab cannot escape into background controls")
	modal.show_modal_dialog("#second")
	modal.update_document(0)
	check(modal.get_focused_id() == "other", "The newest modal takes autofocus")
	modal.set_focus("#inside")
	check(modal.get_focused_id() == "other", "A second modal blocks the first modal's controls")
	modal.close_dialog("#second")
	check(modal.get_focused_id() == "inside", "Closing the second modal restores focus in the first")
	modal.close_dialog("#modal")
	check(modal.get_focused_id() == "behind", "Closing the last modal restores the original game control")
	modal.show_modal_dialog("#second")
	modal.show_modal_dialog("#modal")
	modal.update_document(0)
	modal_clicks.clear()
	for pressed in [true,false]:
		var pointer := InputEventMouseButton.new()
		pointer.position = modal.query_bounds("#inside").get_center()
		pointer.global_position = pointer.position
		pointer.button_index = MOUSE_BUTTON_LEFT
		pointer.pressed = pressed
		viewport.push_input(pointer,true)
	check(modal_clicks == ["inside"], "Reverse opening order keeps the newest dialog's button clickable")
	modal.free()
	var promoted := WevaDocument.new()
	promoted.document_size = Vector2(640,480)
	promoted.html = '<div id="clip"><dialog id="modal"><button id="inside">Confirm</button></dialog></div><div id="cover">Ordinary overlay</div>'
	promoted.css = '#clip{transform:translate(90px,80px);opacity:0;overflow:hidden;width:10px;height:10px}#cover{position:fixed;inset:0;z-index:2147483647;background:red}dialog{position:fixed;left:200px;top:100px;width:150px;height:100px;margin:0;padding:0}'
	viewport.add_child(promoted)
	await get_tree().process_frame
	var before_triangles := promoted.get_triangle_count()
	promoted.show_modal_dialog("#modal")
	promoted.update_document(0)
	check(promoted.query_bounds("#modal").position == Vector2(200,100), "Top-layer geometry escapes the transformed clipped ancestor")
	check(promoted.get_triangle_count() > before_triangles, "Ancestor opacity zero does not suppress modal painting")
	promoted.free()
	for mode in ["normal","underlying","disabled","hidden"]:
		var restored := WevaDocument.new()
		restored.document_size = Vector2(640,480)
		restored.html = '<button id="game">Inventory</button><dialog id="first"><input id="one" value="one"></dialog><dialog id="second"><input id="two" value="two"></dialog>'
		viewport.add_child(restored)
		await get_tree().process_frame
		restored.grab_focus()
		restored.set_focus("#game")
		restored.show_modal_dialog("#first")
		restored.show_modal_dialog("#second")
		if mode == "underlying": restored.close_dialog("#first")
		if mode == "disabled": restored.set_element_attribute("#one","disabled","")
		if mode == "hidden": restored.set_element_style("#one","display","none")
		restored.close_dialog("#second")
		# This native getter flushes pending document work before returning.
		check(restored.get_focused_id() == ("one" if mode == "normal" else ""), "Dialog restoration accepts only available controls: " + mode)
		restored.update_document(0)
		check(restored.get_focused_id() == ("one" if mode == "normal" else ""), "Closing releases hidden focus on update: " + mode)
		text("x")
		check(restored.get_element_value("#one") == ("xone" if mode == "normal" else "one") and restored.get_element_value("#two") == "two", "Typing after close cannot edit hidden controls: " + mode)
		restored.free()
	for mode in ["display","visibility","override","hidden","opacity"]:
		var panel := WevaDocument.new()
		panel.document_size = Vector2(640,480)
		panel.html = '<div id="panel" style="display:{{ PanelDisplay }};visibility:{{ PanelVisibility }};opacity:{{ PanelOpacity }}"><input id="field" value="abc" style="visibility:{{ FieldVisibility }}"></div><aside id="status">Status</aside>'
		panel.css = '#panel[hidden]{display:none!important}#status{width:73px}#panel:focus-within + #status{width:37px}'
		var values := {"FieldVisibility":"inherit","PanelDisplay":"block","PanelVisibility":"visible","PanelOpacity":"1"}
		panel.data = values.duplicate(true)
		viewport.add_child(panel)
		await get_tree().process_frame
		panel.grab_focus()
		panel.set_focus("#field")
		if mode == "display": values.PanelDisplay = "none"
		if mode == "visibility" or mode == "override": values.PanelVisibility = "hidden"
		if mode == "override": values.FieldVisibility = "visible"
		if mode == "hidden": panel.set_element_attribute("#panel","hidden","")
		if mode == "opacity": values.PanelOpacity = "0"
		panel.data = values
		panel.update_document(0)
		var available: bool = mode == "override" or mode == "opacity"
		check(panel.get_focused_id() == ("field" if available else ""), "Bound panel visibility updates focus: " + mode)
		check(panel.get_computed_style("#status","width") == ("37px" if available else "73px"), "Focus-within sibling style settles in the same update: " + mode)
		text("x")
		# Entering the native control selected its first input through keyboard
		# focus navigation; an available field replaces that selection.
		check(panel.get_element_value("#field") == ("x" if available else "abc"), "Bound hidden panels cannot receive typing: " + mode)
		panel.free()
	for mode in ["panel", "modal", "self", "popover"]:
		var inert_panel := WevaDocument.new()
		inert_panel.document_size = Vector2(640,480)
		inert_panel.html = '<div id="panel" inert="{{ Blocked }}"><input id="field" value="abc"><dialog id="modal"><input id="inside" value="abc"></dialog><div popover="manual" id="popup"><input id="popfield" value="abc"></div></div>'
		inert_panel.data = {"Blocked":false}
		viewport.add_child(inert_panel)
		await get_tree().process_frame
		inert_panel.grab_focus()
		inert_panel.set_focus("#field")
		inert_panel.data = {"Blocked":true}
		inert_panel.update_document(0)
		check(inert_panel.get_focused_id() == "", "Inert binding clears focus: " + mode)
		if mode == "modal" or mode == "self":
			inert_panel.show_modal_dialog("#modal")
			inert_panel.set_focus("#inside")
			if mode == "self": inert_panel.set_element_attribute("#modal","inert","")
		if mode == "popover":
			inert_panel.show_popover("#popup")
			inert_panel.set_focus("#popfield")
		inert_panel.update_document(0)
		check(inert_panel.get_focused_id() == ("inside" if mode == "modal" else ""), "Only modal escapes ancestor inertness: " + mode)
		text("x")
		check(inert_panel.get_element_value("#field") == "abc" and inert_panel.get_element_value("#popfield") == "abc" and inert_panel.get_element_value("#inside") == ("xabc" if mode == "modal" else "abc"), "Inert controls cannot receive typing: " + mode)
		inert_panel.free()
	for modal_mode in [false,true]:
		for mode in ["normal","inert","hidden","outside"]:
			var shown := WevaDocument.new()
			shown.document_size = Vector2(640,480)
			shown.html = '<button id="before">Open</button><button id="outside">Outside</button><dialog id="dialog"><button id="first">First</button><button id="second">Second</button></dialog>'
			viewport.add_child(shown)
			await get_tree().process_frame
			shown.grab_focus()
			shown.set_focus("#before")
			if mode == "inert": shown.set_element_attribute("#first","inert","")
			if mode == "hidden": shown.set_element_style("#first","display","none")
			if modal_mode: shown.show_modal_dialog("#dialog")
			else: shown.show_dialog("#dialog")
			check(shown.get_focused_id() == ("second" if mode == "inert" or mode == "hidden" else "first"), "Dialog chooses available focus: %s %s" % [modal_mode,mode])
			if mode == "outside": shown.set_focus("#outside")
			shown.close_dialog("#dialog")
			check(shown.get_focused_id() == ("outside" if mode == "outside" and not modal_mode else "before"), "Dialog restores focus with correct modality: %s %s" % [modal_mode,mode])
			shown.free()
	for mode in ["parent","override","self","auto"]:
		var skipped := WevaDocument.new()
		skipped.document_size = Vector2(640,480)
		skipped.html = '<div id="panel" tabindex="0" style="content-visibility:{{ PanelContent }}"><input id="field" value="abc" style="content-visibility:{{ FieldContent }}"></div>'
		skipped.data = {"PanelContent":"visible","FieldContent":"visible"}
		viewport.add_child(skipped)
		await get_tree().process_frame
		skipped.grab_focus()
		skipped.set_focus("#field")
		skipped.data = {"PanelContent":"auto" if mode == "auto" else ("visible" if mode == "self" else "hidden"),"FieldContent":"hidden" if mode == "self" else "visible"}
		skipped.update_document(0)
		var available: bool = mode == "self" or mode == "auto"
		check(skipped.get_focused_id() == ("field" if available else ""), "Skipped content releases descendant focus: " + mode)
		text("x")
		check(skipped.get_element_value("#field") == ("xabc" if mode == "auto" else "abc"), "Skipped content blocks typing: " + mode)
		key(KEY_BACKSPACE)
		check(skipped.get_element_value("#field") == "abc", "Skipped text also rejects deletion: " + mode)
		skipped.set_focus("#field")
		check(skipped.get_focused_id() == ("field" if available else ""), "Skipped descendant cannot regain focus: " + mode)
		skipped.set_focus("#panel")
		check(skipped.get_focused_id() == "panel", "Content suppression keeps its own box focusable: " + mode)
		skipped.free()
	var paint_cases := {"text":'<input id="control" value="Hello">',"checkbox":'<input id="control" type="checkbox" checked>',"radio":'<input id="control" type="radio" checked>',"range":'<input id="control" type="range">',"select":'<select id="control"><option>Hello</option></select>',"textarea":'<textarea id="control">Hello</textarea>',"button":'<button id="control">Hello</button>'}
	for mode in paint_cases:
		var painted := WevaDocument.new()
		painted.document_size = Vector2(640,480)
		painted.html = paint_cases[mode]
		painted.css = '#control{width:120px;height:40px;box-sizing:border-box;outline:none}'
		viewport.add_child(painted)
		await get_tree().process_frame
		var visible_triangles := painted.get_triangle_count()
		painted.set_element_style("#control","content-visibility","hidden")
		painted.update_document(0)
		var hidden_triangles := painted.get_triangle_count()
		check(hidden_triangles == visible_triangles if mode == "checkbox" or mode == "radio" else hidden_triangles < visible_triangles, "Skipped control paint matches browser distinction: " + mode)
		painted.set_element_style("#control","content-visibility","visible")
		painted.update_document(0)
		check(painted.get_triangle_count() == visible_triangles, "Revealing control restores its drawing: " + mode)
		painted.free()
	var float_cases := [["",Rect2(100,0,300,20)],["width:350px",Rect2(0,100,350,20)],["width:50%",Rect2(100,0,200,20)],["margin-left:20px;margin-right:30px",Rect2(100,0,270,20)],["width:100px;margin:auto",Rect2(200,0,100,20)],["min-width:350px",Rect2(0,100,400,20)],["max-width:150px",Rect2(100,0,150,20)],["padding:10%;border:2px solid",Rect2(100,0,300,104)],["clear:both",Rect2(0,100,400,20)],["margin-top:120px",Rect2(0,120,400,20)],["aspect-ratio:1;height:auto",Rect2(100,0,300,300)],["aspect-ratio:1;height:20px",Rect2(100,0,20,20)]]
	for entry in float_cases:
		var floated := WevaDocument.new()
		floated.document_size = Vector2(640,480)
		floated.html = '<div id="parent"><div id="avatar"></div><div id="content"></div></div>'
		floated.css = 'body{margin:0}#parent{width:400px;display:flow-root}#avatar{float:left;width:100px;height:100px}#content{display:flow-root;height:20px;'+entry[0]+'}'
		viewport.add_child(floated)
		await get_tree().process_frame
		check(floated.query_bounds("#content") == entry[1], "Float-adjacent block geometry: " + entry[0])
		floated.set_element_style("#avatar","width","150px")
		var changed := {"":Rect2(150,0,250,20),"width:350px":Rect2(0,100,350,20),"width:50%":Rect2(150,0,200,20),"margin-left:20px;margin-right:30px":Rect2(150,0,220,20),"width:100px;margin:auto":Rect2(225,0,100,20),"min-width:350px":Rect2(0,100,400,20),"max-width:150px":Rect2(150,0,150,20),"padding:10%;border:2px solid":Rect2(150,0,250,104),"clear:both":Rect2(0,100,400,20),"margin-top:120px":Rect2(0,120,400,20),"aspect-ratio:1;height:auto":Rect2(150,0,250,250),"aspect-ratio:1;height:20px":Rect2(150,0,20,20)}
		check(floated.query_bounds("#content") == changed[entry[0]], "Changing float width invalidates neighbouring geometry: " + entry[0])
		floated.free()
	var wrapped := WevaDocument.new()
	wrapped.document_size = Vector2(640,480)
	wrapped.html = '<div id="p"><div id="a"></div><div id="b"></div><div id="c"><i id="tile"></i><i></i><i></i><i></i></div></div>'
	wrapped.css = 'body{margin:0}#p{width:400px;display:flow-root}#a{float:left;width:100px;height:100px}#b{float:right;clear:left;width:150px;height:200px}#c{display:flow-root;font-size:0;line-height:0}i{display:inline-block;width:180px;height:40px;vertical-align:top}'
	viewport.add_child(wrapped)
	await get_tree().process_frame
	for height in [100,200,100]:
		wrapped.set_element_style("#a","height",str(height)+"px")
		check(wrapped.query_bounds("#c") == Rect2(100,0,300 if height == 200 else 150,160), "Wrapped panel avoids lower float after height change")
		check(wrapped.query_bounds("#tile") == Rect2(100,0,180,40), "Reflow keeps outer floats outside panel contents")
	wrapped.free()
	for display in ["flow-root","block"]:
		for height in [0,20]:
			var cleared := WevaDocument.new()
			cleared.document_size = Vector2(640,480)
			cleared.html = '<div id="p"><div id="f"></div><div id="c"></div><div id="after"></div></div>'
			cleared.css = 'html,body{margin:0}#p{width:400px;display:'+display+'}#f{float:left;width:100px;height:80px}#c{height:'+str(height)+'px;clear:left;margin-top:30px;margin-bottom:10px}#after{height:10px}'
			viewport.add_child(cleared)
			await get_tree().process_frame
			for margin in [30,100,-30,30]:
				cleared.set_element_style("#c","margin-top",str(margin)+"px")
				var cleared_top := 100 if display == "flow-root" and margin == 100 else 80
				var following_top := cleared_top+30 if height == 20 else cleared_top+(10 if margin < 0 else 0)
				check(cleared.query_bounds("#c") == Rect2(0,cleared_top,400,height), "Clearance absorbs margin through live updates")
				check(cleared.query_bounds("#after") == Rect2(0,following_top,400,10), "Clearing block preserves following sibling collapse")
			cleared.free()
	var remembered := WevaDocument.new()
	remembered.document_size = Vector2(640,480)
	remembered.html = '<input id="a" value="abcdef"><input id="b" value="second"><button id="apply">Apply</button>'
	viewport.add_child(remembered)
	await get_tree().process_frame
	check(remembered.get_element_selection("#a") == Vector2i.ZERO, "Markup starts with a zero selection")
	remembered.set_focus("#a")
	check(remembered.get_element_selection("#a") == Vector2i.ZERO, "Programmatic first focus preserves the initial selection")
	remembered.set_element_value("#b","replacement")
	check(remembered.get_element_selection("#b") == Vector2i(11,11), "First value assignment updates an unvisited field's cursor")
	remembered.set_element_selection("#a",4,1)
	for target in ["#b","#a","#apply","#a"]:
		remembered.set_focus(target)
		check(remembered.get_element_selection("#a") == Vector2i(4,1), "Each field retains its backwards selection across focus changes")
	remembered.set_focus("#b")
	remembered.set_element_value("#a","xy")
	check(remembered.get_element_selection("#a") == Vector2i(2,2), "Changing a blurred value moves its saved cursor to the end")
	remembered.set_focus("#a")
	check(remembered.get_element_selection("#a") == Vector2i(2,2), "Refocusing restores the changed value's cursor")
	remembered.set_element_selection("#a",0,1)
	remembered.set_focus("#b")
	remembered.set_element_value("#a","xy")
	check(remembered.get_element_selection("#a") == Vector2i(0,1), "No-op value assignment preserves a blurred selection")
	check(remembered.set_element_selection_without_focus("#a",2,0), "Prepare a backward selection without focusing")
	check(remembered.get_focused_id() == "b", "Non-focusing selection preserves the active field")
	check(remembered.get_element_selection("#a") == Vector2i(2,0), "Non-focusing selection stores both endpoints")
	remembered.free()
	# Chrome 152 default-value/selection matrix, using the public Godot API.
	var selection_cases: Array = [
		{"tag":"input","dirty":false,"focused":false,"action":"reset","value":"abcdef","selection":[1,4,"backward"],"active":"b"},
		{"tag":"input","dirty":false,"focused":false,"action":"default","value":"new default","selection":[1,4,"backward"],"active":"b"},
		{"tag":"input","dirty":false,"focused":false,"action":"same-default","value":"abcdef","selection":[1,4,"backward"],"active":"b"},
		{"tag":"input","dirty":false,"focused":false,"action":"attribute","value":"new attribute","selection":[1,4,"backward"],"active":"b"},
		{"tag":"input","dirty":false,"focused":true,"action":"reset","value":"abcdef","selection":[1,4,"backward"],"active":"a"},
		{"tag":"input","dirty":false,"focused":true,"action":"default","value":"new default","selection":[0,0,"forward"],"active":"a"},
		{"tag":"input","dirty":false,"focused":true,"action":"same-default","value":"abcdef","selection":[1,4,"backward"],"active":"a"},
		{"tag":"input","dirty":false,"focused":true,"action":"attribute","value":"new attribute","selection":[0,0,"forward"],"active":"a"},
		{"tag":"input","dirty":true,"focused":false,"action":"reset","value":"abcdef","selection":[6,6,"forward"],"active":"b"},
		{"tag":"input","dirty":true,"focused":false,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"tag":"input","dirty":true,"focused":false,"action":"same-default","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"tag":"input","dirty":true,"focused":false,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"tag":"input","dirty":true,"focused":true,"action":"reset","value":"abcdef","selection":[6,6,"forward"],"active":"a"},
		{"tag":"input","dirty":true,"focused":true,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"tag":"input","dirty":true,"focused":true,"action":"same-default","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"tag":"input","dirty":true,"focused":true,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"tag":"textarea","dirty":false,"focused":false,"action":"reset","value":"abcdef","selection":[1,4,"backward"],"active":"b"},
		{"tag":"textarea","dirty":false,"focused":false,"action":"default","value":"new default","selection":[0,0,"forward"],"active":"b"},
		{"tag":"textarea","dirty":false,"focused":false,"action":"same-default","value":"abcdef","selection":[0,0,"forward"],"active":"b"},
		{"tag":"textarea","dirty":false,"focused":false,"action":"attribute","value":"abcdef","selection":[1,4,"backward"],"active":"b"},
		{"tag":"textarea","dirty":false,"focused":true,"action":"reset","value":"abcdef","selection":[1,4,"backward"],"active":"a"},
		{"tag":"textarea","dirty":false,"focused":true,"action":"default","value":"new default","selection":[0,0,"forward"],"active":"a"},
		{"tag":"textarea","dirty":false,"focused":true,"action":"same-default","value":"abcdef","selection":[0,0,"forward"],"active":"a"},
		{"tag":"textarea","dirty":false,"focused":true,"action":"attribute","value":"abcdef","selection":[1,4,"backward"],"active":"a"},
		{"tag":"textarea","dirty":true,"focused":false,"action":"reset","value":"abcdef","selection":[6,6,"forward"],"active":"b"},
		{"tag":"textarea","dirty":true,"focused":false,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"tag":"textarea","dirty":true,"focused":false,"action":"same-default","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"tag":"textarea","dirty":true,"focused":false,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"tag":"textarea","dirty":true,"focused":true,"action":"reset","value":"abcdef","selection":[6,6,"forward"],"active":"a"},
		{"tag":"textarea","dirty":true,"focused":true,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"tag":"textarea","dirty":true,"focused":true,"action":"same-default","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"tag":"textarea","dirty":true,"focused":true,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"x","tag":"input","dirty":false,"focused":false,"action":"default","value":"x","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"x","tag":"input","dirty":false,"focused":false,"action":"attribute","value":"x","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"x","tag":"input","dirty":false,"focused":true,"action":"default","value":"x","selection":[0,0,"forward"],"active":"a"},
		{"replacement":"x","tag":"input","dirty":false,"focused":true,"action":"attribute","value":"x","selection":[0,0,"forward"],"active":"a"},
		{"replacement":"x","tag":"input","dirty":true,"focused":false,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"x","tag":"input","dirty":true,"focused":false,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"x","tag":"input","dirty":true,"focused":true,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"x","tag":"input","dirty":true,"focused":true,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"x","tag":"textarea","dirty":false,"focused":false,"action":"default","value":"x","selection":[0,0,"forward"],"active":"b"},
		{"replacement":"x","tag":"textarea","dirty":false,"focused":false,"action":"attribute","value":"abcdef","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"x","tag":"textarea","dirty":false,"focused":true,"action":"default","value":"x","selection":[0,0,"forward"],"active":"a"},
		{"replacement":"x","tag":"textarea","dirty":false,"focused":true,"action":"attribute","value":"abcdef","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"x","tag":"textarea","dirty":true,"focused":false,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"x","tag":"textarea","dirty":true,"focused":false,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"x","tag":"textarea","dirty":true,"focused":true,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"x","tag":"textarea","dirty":true,"focused":true,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"","tag":"input","dirty":false,"focused":false,"action":"default","value":"","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"","tag":"input","dirty":false,"focused":false,"action":"attribute","value":"","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"","tag":"input","dirty":false,"focused":true,"action":"default","value":"","selection":[0,0,"forward"],"active":"a"},
		{"replacement":"","tag":"input","dirty":false,"focused":true,"action":"attribute","value":"","selection":[0,0,"forward"],"active":"a"},
		{"replacement":"","tag":"input","dirty":true,"focused":false,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"","tag":"input","dirty":true,"focused":false,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"","tag":"input","dirty":true,"focused":true,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"","tag":"input","dirty":true,"focused":true,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"","tag":"textarea","dirty":false,"focused":false,"action":"default","value":"","selection":[0,0,"forward"],"active":"b"},
		{"replacement":"","tag":"textarea","dirty":false,"focused":false,"action":"attribute","value":"abcdef","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"","tag":"textarea","dirty":false,"focused":true,"action":"default","value":"","selection":[0,0,"forward"],"active":"a"},
		{"replacement":"","tag":"textarea","dirty":false,"focused":true,"action":"attribute","value":"abcdef","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"","tag":"textarea","dirty":true,"focused":false,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"","tag":"textarea","dirty":true,"focused":false,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"","tag":"textarea","dirty":true,"focused":true,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"","tag":"textarea","dirty":true,"focused":true,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"abcdef\n","tag":"input","dirty":false,"focused":false,"action":"default","value":"abcdef","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"abcdef\n","tag":"input","dirty":false,"focused":false,"action":"attribute","value":"abcdef","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"abcdef\n","tag":"input","dirty":false,"focused":true,"action":"default","value":"abcdef","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"abcdef\n","tag":"input","dirty":false,"focused":true,"action":"attribute","value":"abcdef","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"abcdef\n","tag":"input","dirty":true,"focused":false,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"abcdef\n","tag":"input","dirty":true,"focused":false,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"abcdef\n","tag":"input","dirty":true,"focused":true,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"abcdef\n","tag":"input","dirty":true,"focused":true,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"abcdef\n","tag":"textarea","dirty":false,"focused":false,"action":"default","value":"abcdef\n","selection":[0,0,"forward"],"active":"b"},
		{"replacement":"abcdef\n","tag":"textarea","dirty":false,"focused":false,"action":"attribute","value":"abcdef","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"abcdef\n","tag":"textarea","dirty":false,"focused":true,"action":"default","value":"abcdef\n","selection":[0,0,"forward"],"active":"a"},
		{"replacement":"abcdef\n","tag":"textarea","dirty":false,"focused":true,"action":"attribute","value":"abcdef","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"abcdef\n","tag":"textarea","dirty":true,"focused":false,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"abcdef\n","tag":"textarea","dirty":true,"focused":false,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"b"},
		{"replacement":"abcdef\n","tag":"textarea","dirty":true,"focused":true,"action":"default","value":"edited","selection":[1,4,"backward"],"active":"a"},
		{"replacement":"abcdef\n","tag":"textarea","dirty":true,"focused":true,"action":"attribute","value":"edited","selection":[1,4,"backward"],"active":"a"}
	]
	for row in selection_cases:
		var field := WevaDocument.new()
		field.document_size = Vector2(640,480)
		field.html = "<form id=f>" + ("<textarea id=a>abcdef</textarea>" if row.tag == "textarea" else "<input id=a value=abcdef>") + "<button id=b type=button>Apply</button></form>"
		viewport.add_child(field)
		field.update_document(0)
		if row.dirty: field.set_element_value("#a", "edited")
		field.set_element_selection("#a",4,1)
		if not row.focused: field.set_focus("#b")
		if row.action == "reset": field.reset_form("#f")
		elif row.action == "attribute": field.set_element_attribute("#a","value",row.get("replacement","new attribute"))
		else:
			var value: String = "abcdef" if row.action == "same-default" else row.get("replacement","new default")
			if row.tag == "textarea": field.set_element_text("#a",value)
			else: field.set_element_attribute("#a","value",value)
		var expected := Vector2i(row.selection[0],row.selection[1])
		if row.selection[2] == "backward": expected = Vector2i(expected.y,expected.x)
		var label := "%s dirty=%s focused=%s %s" % [row.tag,row.dirty,row.focused,row.action]
		check(field.get_element_value("#a") == row.value, "Default lifecycle value: " + label)
		check(field.get_element_selection("#a") == expected, "Default lifecycle selection: " + label)
		check(field.get_focused_id() == row.active, "Default lifecycle focus: " + label)
		field.free()
	viewport.free()
	print("godot form state: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
