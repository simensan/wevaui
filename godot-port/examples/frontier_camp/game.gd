extends Control

const CampState = preload("res://camp_state.gd")
var state := CampState.new()
@onready var ui: WevaView = $UI
var world_actions := 0
var settings_open := false
var settings_applied := 0
var capture_prefix := ""

func _ready() -> void:
	# This is the entire setup: markup lives on the UI node in the Inspector.
	ui.bind_state(state.model, self, state.changed)
	ui.data_changed.connect(_on_data_changed)
	$Clock.timeout.connect(state.tick)
	_apply_settings()
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--capture="):
			capture_prefix = arg.trim_prefix("--capture=")
	if "--check" in OS.get_cmdline_user_args():
		var suite = load("res://tests/integration.gd").new()
		await suite.run(self)
	elif "--perf" in OS.get_cmdline_user_args():
		var benchmark = load("res://tests/performance.gd").new()
		await benchmark.run(self)

# HTML names these methods with on-click. No manual signal per button.
func forage(_id: String) -> void:
	state.forage()

func craft(_id: String) -> void:
	state.craft()

func sort_items(_id: String) -> void:
	state.sort_items()

func use_item(element_id: String) -> void:
	# Stable data-key identity survives sorting and removing depleted stacks.
	var row := ui.get_row("#" + element_id)
	if not row.is_empty():
		state.use_item(row.key)

func open_settings(_id: String) -> void:
	settings_open = true
	ui.show_modal_dialog("#settings")
	ui.set_focus("#player-name")

func close_settings(_id: String) -> void:
	settings_open = false
	ui.close_dialog("#settings")
	ui.set_focus("#settings-button")

func _on_data_changed(_path: String, _text: String) -> void:
	# Native data-model already wrote a typed value into state.model.
	_apply_settings()

func _apply_settings() -> void:
	settings_applied += 1
	AudioServer.set_bus_volume_db(0, linear_to_db(maxf(0.0001, state.model.Settings.Volume / 100.0)))
	AudioServer.set_bus_mute(0, not state.model.Settings.Music)

func _unhandled_input(event: InputEvent) -> void:
	# Accepted UI clicks and text edits never reach gameplay.
	if settings_open:
		if event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
			close_settings("")
		get_viewport().set_input_as_handled()
		return
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		world_actions += 1
		state.forage()
		get_viewport().set_input_as_handled()
	elif event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_E:
			world_actions += 1
			state.forage()
			get_viewport().set_input_as_handled()
		elif event.keycode == KEY_H:
			state.take_damage()
			get_viewport().set_input_as_handled()
