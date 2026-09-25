extends Node

var doc: WevaDocument
var report := ""
var capturing := false
var native: LineEdit

func record(kind: String, text := "") -> void:
	var row := {"kind": kind, "text": text, "value": native.text if native else doc.get_element_value("#field"), "composing": native.has_ime_text() if native else doc.has_composition()}
	row["frame"] = Engine.get_process_frames()
	if native:
		row["focused"] = native.has_focus()
		row["editing"] = native.is_editing()
		row["window_focused"] = DisplayServer.window_is_focused()
		row["screen_rect"] = str(native.get_global_rect())
	print("IME_PROBE ", JSON.stringify(row))
	if not report.is_empty():
		var file := FileAccess.open(report, FileAccess.READ_WRITE if FileAccess.file_exists(report) else FileAccess.WRITE)
		file.seek_end()
		file.store_line(JSON.stringify(row))

func _ready() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--ime-report="):
			report = argument.trim_prefix("--ime-report=")
	get_window().title = "Weva IME Native Probe"
	doc = WevaDocument.new()
	doc.interactive = not "--native-line-edit" in OS.get_cmdline_user_args()
	doc.document_size = Vector2(640, 360)
	doc.css = "body{margin:0;padding:32px;background:#172131;color:white;font-size:22px}input{width:360px;height:50px;font-size:24px;padding:8px}p{margin:16px 0}"
	doc.html = '<p>IME composition</p><input id="field" data-model="Name"><p id="label">{{ Name }}</p>'
	doc.data = {"Name": ""}
	add_child(doc)
	doc.composition_started.connect(func(_id, text): record("start", text))
	doc.composition_updated.connect(func(_id, text): record("update", text); _capture.call_deferred("preedit"))
	doc.composition_ended.connect(func(_id, text): record("end", text); _capture.call_deferred("committed"))
	doc.text_entered.connect(func(_id, text): record("text", text))
	doc.value_changed.connect(func(_id, text): record("input", text))
	await get_tree().process_frame
	await get_tree().process_frame
	if "--native-line-edit" in OS.get_cmdline_user_args():
		native = LineEdit.new()
		native.caret_blink = true
		native.position = Vector2(32, 200)
		native.size = Vector2(360, 50)
		add_child(native)
		native.text_changed.connect(func(text): record("native-input", text))
		native.grab_focus()
		native.edit()
		await get_tree().process_frame
	else:
		doc.set_focus("#field")
	record("ready")
	await get_tree().create_timer(60).timeout
	get_tree().quit()

func _notification(what: int) -> void:
	if what == NOTIFICATION_OS_IME_UPDATE and doc:
		record("os-preedit", DisplayServer.ime_get_text())
		# Record the control's state after notification propagation completes.
		record.call_deferred("state")

func _input(event: InputEvent) -> void:
	if event is InputEventKey and doc:
		record("key", "%d:%d:%s" % [event.keycode, event.unicode, event.pressed])

func _capture(label: String) -> void:
	if capturing or report.is_empty():
		return
	capturing = true
	await RenderingServer.frame_post_draw
	get_viewport().get_texture().get_image().save_png(report + "." + label + ".png")
	capturing = false
