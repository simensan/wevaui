extends Node
## A separate entry keeps lifecycle measurements ahead of the first UI load.

func _ready() -> void:
	if "--lifecycle" in OS.get_cmdline_user_args():
		var suite = load("res://tests/lifecycle.gd").new()
		await suite.run(get_tree())
	else:
		# Keep the loading screen responsive while shared fallback faces load.
		# Older addon versions retain the ordinary synchronous startup path.
		if ClassDB.class_has_method("WevaDocument", "warmup_fonts_step"):
			var loading := Label.new()
			loading.text = "Loading camp…"
			loading.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
			loading.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
			add_child(loading)
			loading.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
			await get_tree().process_frame
			while not ClassDB.class_call_static("WevaDocument", "warmup_fonts_step"):
				await get_tree().process_frame
		get_tree().change_scene_to_file.call_deferred("res://main.tscn")
