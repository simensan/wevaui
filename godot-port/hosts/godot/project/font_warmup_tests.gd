extends Node

func _ready() -> void:
	var steps := 0
	var durations: Array[float] = []
	var complete := false
	var nodes_before := Performance.get_monitor(Performance.OBJECT_NODE_COUNT)
	while not complete and steps < 64:
		var start := Time.get_ticks_usec()
		complete = ClassDB.class_call_static("WevaDocument", "warmup_fonts_step")
		durations.append(float(Time.get_ticks_usec() - start) / 1000.0)
		steps += 1
		await get_tree().process_frame
	var failures := 0
	for ok in [complete, steps < 64, ClassDB.class_call_static("WevaDocument", "warmup_fonts_step"),
		Performance.get_monitor(Performance.OBJECT_NODE_COUNT) == nodes_before]:
		if not ok:
			failures += 1
	print("Font warmup: 4 checks, ", failures, " failures; step ms ", durations)
	get_tree().quit(1 if failures else 0)
