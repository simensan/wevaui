# Standalone Godot reproduction: no Weva class, addon or GDExtension is loaded.
extends Node

var field: LineEdit
var report := ""

func record(kind: String, text := "") -> void:
	var row := {"kind": kind, "text": text, "value": field.text,
		"composing": field.has_ime_text(), "frame": Engine.get_process_frames(),
		"focused": field.has_focus(), "editing": field.is_editing()}
	print("IME_PROBE ", JSON.stringify(row))
	if not report.is_empty():
		var file := FileAccess.open(report, FileAccess.READ_WRITE if FileAccess.file_exists(report) else FileAccess.WRITE)
		file.seek_end()
		file.store_line(JSON.stringify(row))

func _ready() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--ime-report="):
			report = argument.trim_prefix("--ime-report=")
	get_window().title = "Weva IME Native Probe" # Existing isolated X11 runner's window selector.
	var label := Label.new()
	label.position = Vector2(32, 32)
	label.text = "Native Godot LineEdit\nType Chinese text, commit it, then undo."
	add_child(label)
	field = LineEdit.new()
	field.position = Vector2(32, 200)
	field.size = Vector2(360, 50)
	field.caret_blink = true
	add_child(field)
	field.text_changed.connect(func(text): record("native-input", text))
	await get_tree().process_frame
	field.grab_focus()
	field.edit()
	await get_tree().process_frame
	record("ready")
	await get_tree().create_timer(60).timeout
	get_tree().quit()

func _notification(what: int) -> void:
	if what == NOTIFICATION_OS_IME_UPDATE and field:
		record("os-preedit", DisplayServer.ime_get_text())
		# The root receives this before LineEdit updates its own preedit state.
		record.call_deferred("state")

func _input(event: InputEvent) -> void:
	if event is InputEventKey and field:
		record("key", "%d:%d:%s" % [event.keycode, event.unicode, event.pressed])
