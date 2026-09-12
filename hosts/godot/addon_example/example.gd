extends Control

const ASSETS := "res://addons/weva/example/"
var ui: WevaDocument
var player := {"Name": "Ada", "Score": 0}

func _ready() -> void:
	ui = WevaDocument.new()
	ui.base_path = ASSETS
	ui.css = FileAccess.get_file_as_string(ASSETS + "ui.css")
	ui.html = FileAccess.get_file_as_string(ASSETS + "ui.html")
	ui.data = {"Player": player}
	ui.set_controller(self)
	add_child(ui)
	if "--weva-example-test" in OS.get_cmdline_user_args():
		_self_test()

func award_point(_id: String) -> void:
	player.Score += 1
	ui.data = {"Player": player}

func reset_score(_id: String) -> void:
	player.Score = 0
	ui.data = {"Player": player}

# Executed by the package check in a fresh project and from its exported PCK.
func _self_test() -> void:
	# The root window applies its configured viewport after scene _ready.
	await get_tree().process_frame
	await get_tree().process_frame
	ui.update_document(0)
	var failures := 0
	if not ui.get_missing_assets().is_empty() or ui.get_draw_count() == 0 or ui.query_bounds("header img").size != Vector2(36, 36):
		printerr("FAIL  example assets and drawing")
		failures += 1
	var at := ui.query_bounds("#award").get_center()
	ui.set_pointer(at, 1)
	ui.set_pointer(at, 0)
	ui.update_document(0)
	if player.Score != 1 or ui.query_text("#score") != "1":
		printerr("FAIL  example button updates the bound score")
		failures += 1
	ui.set_focus("#name")
	ui.select_all()
	ui.send_text("Grace")
	ui.update_document(0)
	if player.Name != "Grace" or ui.query_text("#greeting") != "Hello, Grace":
		printerr("FAIL  example text field writes back to game state")
		failures += 1
	ui.select_all()
	ui.paste_text("😀".repeat(21))
	ui.update_document(0)
	if player.Name != "😀".repeat(20) or ui.get_element_value("#name") != player.Name:
		printerr("FAIL  example maxlength constrains pasted Unicode and its binding")
		failures += 1
	ui.undo()
	ui.update_document(0)
	if player.Name != "Grace" or ui.query_text("#greeting") != "Hello, Grace":
		printerr("FAIL  example constrained paste is one undo step")
		failures += 1
	reset_score("")
	ui.update_document(0)
	if ui.query_text("#score") != "0":
		printerr("FAIL  example reset refreshes the binding")
		failures += 1
	ui.set_focus("#award")
	var event := InputEventKey.new()
	event.keycode = KEY_SPACE
	event.pressed = true
	get_viewport().push_input(event)
	if player.Score != 0:
		printerr("FAIL  example keyboard action waits for Space release")
		failures += 1
	event = InputEventKey.new()
	event.keycode = KEY_SPACE
	event.pressed = false
	get_viewport().push_input(event)
	ui.update_document(0)
	if player.Score != 1 or ui.query_text("#score") != "1":
		printerr("FAIL  example keyboard action reaches controller and binding")
		failures += 1
	if not ui.has_element('#name[value="Ada"]'):
		printerr("FAIL  example editing preserves the markup default")
		failures += 1
	at = ui.query_bounds("#restore").get_center()
	ui.set_pointer(at, 1)
	ui.set_pointer(at, 0)
	ui.update_document(0)
	if player.Name != "Ada" or player.Score != 0 or ui.query_text("#greeting") != "Hello, Ada":
		printerr("FAIL  example native reset restores defaults, game state and its controller action")
		failures += 1
	# Exercise embedded ICU in installed/exported binaries, where no source
	# tree or system ICU data package is available to satisfy the search.
	var choices := WevaDocument.new()
	choices.html = '<select id="choices" multiple size="3" data-model="Choice"><option value="a">Alpha</option><optgroup label="Cities"><option id="city" value="city" label="Évry">Internal name</option></optgroup></select>'
	choices.data = {"Choice":"a"}
	add_child(choices)
	choices.update_document(0)
	choices.set_focus("#choices")
	event = InputEventKey.new()
	event.keycode = KEY_E
	event.unicode = 101
	event.pressed = true
	get_viewport().push_input(event)
	if choices.get_element_value("#choices") != "city" or choices.data.Choice != "city":
		printerr("FAIL  exported native select searches accented labels and updates binding")
		failures += 1
	if choices.get_element_text("#city") != "Internal name":
		printerr("FAIL  exported option label preserves DOM text")
		failures += 1
	choices.css = 'html,body{margin:0}select{display:block;width:220px;height:96px;padding:0;border:0}option{height:24px;padding:0}'
	var list_html := '<select id="choices" multiple size="4" data-model="Choice">'
	for i in range(20):
		list_html += '<option id="row%d" value="%d">Row %d</option>' % [i,i,i]
	choices.data = {"Choice":""}
	choices.html = list_html + '</select>'
	choices.update_document(0)
	var commits: Array[String] = []
	choices.value_changed.connect(func(_id, _value): commits.append("input"))
	choices.value_committed.connect(func(_id, _value): commits.append("change"))
	choices.set_pointer(choices.query_bounds("#row1").get_center(),1)
	choices.set_pointer(choices.query_bounds("#row2").get_center(),1)
	choices.set_pointer(Vector2(110,200),1)
	for i in range(20):
		choices.update_document(0.05)
	if choices.get_element_scroll("#choices").y <= 0 or choices.get_element_value("#choices") != "1,2" or not commits.is_empty():
		printerr("FAIL  exported held select scrolls without changing selection or committing")
		failures += 1
	choices.set_pointer(choices.query_bounds("#row18").get_center(),1)
	choices.set_pointer(Vector2(110,200),0)
	var expected := PackedStringArray()
	for i in range(1,19):
		expected.append(str(i))
	if choices.get_element_value("#choices") != ",".join(expected) or choices.data.Choice != ",".join(expected) or commits != ["input","change"]:
		printerr("FAIL  exported autoscroll commits the reentered range and model once")
		failures += 1
	choices.css = 'html,body{margin:0}textarea{display:block;width:160px;height:96px;padding:0;border:0;font-size:16px;line-height:24px;white-space:pre;overflow:auto}'
	var note := ""
	for i in 20:
		note += ("\n" if i else "") + str(i) + " " + "á😀b ".repeat(12)
	choices.data = {"Value":note}
	choices.html = '<textarea id="field" data-model="Value"></textarea>'
	choices.set_focus("#field")
	choices.set_element_selection("#field",0,0)
	choices.update_document(0)
	commits.clear()
	choices.set_pointer(Vector2(20,16),1)
	choices.set_pointer(Vector2(40,16),1)
	choices.set_pointer(Vector2(400,300),1)
	for i in 60:
		choices.update_document(0.05)
	var selection := choices.get_element_selection("#field")
	choices.set_pointer(Vector2(400,300),0)
	var scroll := choices.get_element_scroll("#field")
	if selection.y != note.to_utf8_buffer().size() or scroll.x <= 0 or scroll.y <= 0 or choices.data.Value != note or not commits.is_empty():
		printerr("FAIL  exported text selection scrolls both axes without editing the model: selection %s/%d, scroll %s, value bytes %d, model bytes %d, events %s" % [selection,note.to_utf8_buffer().size(),scroll,choices.get_element_value("#field").to_utf8_buffer().size(),str(choices.data.Value).to_utf8_buffer().size(),commits])
		failures += 1
	choices.paste_text("Z")
	var prefix := note.to_utf8_buffer().slice(0,selection.x).get_string_from_utf8()
	if choices.get_element_value("#field") != prefix+"Z" or choices.data.Value != prefix+"Z":
		printerr("FAIL  exported text autoscroll preserves Unicode source selection for editing")
		failures += 1
	choices.undo()
	if choices.get_element_value("#field") != note:
		printerr("FAIL  exported continuous selection adds no undo steps: restored %d/%d bytes" % [choices.get_element_value("#field").to_utf8_buffer().size(),note.to_utf8_buffer().size()])
		failures += 1
	choices.free()
	print("weva addon example: 17 checks, %d failures" % failures)
	get_tree().quit(1 if failures else 0)
