extends SceneTree

# Real rendered frames, with a background-only control. GPU timestamps are
# asynchronous; compare warmed phase distributions, not same-frame CPU/GPU sums.
var sample: Control
var doc: WevaDocument
var results: Array[Dictionary] = []
var frames := 600
var warmup := 120
var rounds := 3
var output := ""
var gpu_timing := true

func _initialize() -> void:
	call_deferred("run")

func summary(values: Array[float]) -> Dictionary:
	var ordered := values.duplicate()
	ordered.sort()
	var total := 0.0
	for value in values:
		total += value
	return {"mean_ms": total / values.size(), "median_ms": ordered[ordered.size() / 2],
		"p95_ms": ordered[mini(ordered.size() - 1, ceili(ordered.size() * 0.95) - 1)]}

func run() -> void:
	output = OS.get_environment("WEVA_RENDER_BENCH_OUT")
	if output.is_empty() or DisplayServer.get_name() == "headless":
		printerr("FAIL requires a real rendering driver and WEVA_RENDER_BENCH_OUT")
		quit(1)
		return
	if OS.has_environment("WEVA_RENDER_BENCH_FRAMES"):
		frames = maxi(1, int(OS.get_environment("WEVA_RENDER_BENCH_FRAMES")))
	if OS.has_environment("WEVA_RENDER_BENCH_ROUNDS"):
		rounds = maxi(1, int(OS.get_environment("WEVA_RENDER_BENCH_ROUNDS")))
	gpu_timing = OS.get_environment("WEVA_RENDER_BENCH_GPU") != "0"
	Engine.max_fps = maxi(0, int(OS.get_environment("WEVA_RENDER_BENCH_FPS")))
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	root.size = Vector2i(1280, 720)
	sample = load("res://samples/western_survival/survival.tscn").instantiate()
	root.add_child(sample)
	doc = sample.get("ui")
	sample.set_process(false)
	sample.set_process_unhandled_input(false)
	doc.set_process(false)
	doc.interactive = false
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), gpu_timing)
	for round_index in rounds:
		var cases := ["background", "idle_hud", "updating_hud", "inventory", "inventory_selection"]
		if round_index % 2:
			cases.reverse()
		for case_name in cases:
			await measure(case_name, round_index)
	var receipt := {"schema": 1, "engine": Engine.get_version_info(),
		"adapter": RenderingServer.get_video_adapter_name(),
		"driver": RenderingServer.get_current_rendering_driver_name(),
		"method": RenderingServer.get_current_rendering_method(),
		"viewport": [root.size.x, root.size.y], "gpu_timing_enabled": gpu_timing,
		"frames": frames, "warmup": warmup, "rounds": rounds,
		"frame_limit": Engine.max_fps, "results": results}
	var file := FileAccess.open(output, FileAccess.WRITE)
	if file == null:
		printerr("FAIL cannot write benchmark output: ", output)
		quit(1)
		return
	file.store_string(JSON.stringify(receipt, "\t"))
	file.close()
	quit()

func measure(case_name: String, round_index: int) -> void:
	sample.set("stamina", 100.0)
	sample.call("_render_stamina")
	sample.call("select_inventory_item", "item-0")
	doc.visible = case_name != "background"
	if case_name.begins_with("inventory"):
		sample.call("open_inventory")
	else:
		sample.call("close_inventory")
	doc.update_document(0)
	var wall: Array[float] = []
	var update: Array[float] = []
	var cpu: Array[float] = []
	var setup: Array[float] = []
	var gpu: Array[float] = []
	var calls: Array[float] = []
	var frame_ids: Array[int] = []
	var core_stats: Array[Dictionary] = []
	var collect_core := OS.get_environment("WEVA_RENDER_BENCH_CORE_STATS") == "1"
	var previous := Time.get_ticks_usec()
	for i in warmup + frames:
		await process_frame
		var start := Time.get_ticks_usec()
		var frame_id := Engine.get_process_frames()
		var frame_ms := (start - previous) / 1000.0
		previous = start
		if case_name == "updating_hud":
			sample.set("stamina", float(30 + i % 70))
			sample.call("_render_stamina")
		elif case_name == "inventory_selection":
			sample.call("select_inventory_item", "item-%d" % (i % 12))
		if case_name != "background":
			doc.update_document(1.0 / 60.0)
		var update_ms := (Time.get_ticks_usec() - start) / 1000.0
		if collect_core and i >= warmup:
			core_stats.append(doc.get_stats())
		await RenderingServer.frame_post_draw
		if i >= warmup:
			frame_ids.append(frame_id)
			wall.append(frame_ms)
			update.append(update_ms)
			calls.append(Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
			setup.append(RenderingServer.get_frame_setup_time_cpu())
			if gpu_timing:
				cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid()))
				gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid()))
	var result := {"case": case_name, "round": round_index,
		"wall": summary(wall), "controller_and_update": summary(update),
		"render_setup_cpu": summary(setup),
		"draw_calls_median": summary(calls).median_ms,
		"core_draws": doc.get_draw_count() if doc.visible else 0,
		"triangles": doc.get_triangle_count() if doc.visible else 0,
		"raw": {"wall_ms": wall, "update_ms": update, "render_cpu_ms": cpu,
			"render_gpu_ms": gpu, "render_setup_cpu_ms": setup,
			"draw_calls": calls, "frame_ids": frame_ids}}
	if gpu_timing:
		result["render_cpu"] = summary(cpu)
		result["render_gpu"] = summary(gpu)
		result["gpu_timestamps_available"] = gpu.any(func(value): return value > 0)
	if collect_core:
		result["core_stats_diagnostic"] = core_stats
	results.append(result)
	print("RENDER_BENCH ", case_name, " round ", round_index, " wall ", result.wall,
		" update ", result.controller_and_update, " GPU ", result.get("render_gpu", {}))
	if round_index == 0:
		var capture_error := root.get_texture().get_image().save_png(output + "." + case_name + ".png")
		if capture_error != OK:
			printerr("FAIL screenshot capture: ", capture_error)
			quit(1)
