extends RefCounted
## Native rendered benchmark. Run an exported release game with -- --perf.
## API timing includes actions, binding refresh and document update. Frame
## timing also includes deferred callbacks, canvas submission and presentation.

const CASES := ["ui_disabled", "idle", "clock_1hz", "vitals_10hz", "clock_update", "vitals_update", "redundant_signal", "hover", "inventory_sort", "settings_typing", "settings_slider", "settings_toggle"]
var game: Control
var ui: WevaView
var tree: SceneTree
var viewport: Viewport
var failed := false
var pending := false
var refreshes := 0
var events: Dictionary = {}
var auto_update := false
var frames := 600
var warmups := 120
var output := ""
var capture_enabled := false

func require(ok: bool, label: String) -> void:
	if not ok:
		failed = true
		printerr("FAIL frontier performance: " + label)

func stats(values: Array[float]) -> Dictionary:
	if values.is_empty(): return {"samples": 0}
	var sorted := values.duplicate()
	sorted.sort()
	var total := 0.0
	for value in values: total += value
	return {"samples": values.size(), "mean_ms": total / values.size(),
		"median_ms": sorted[sorted.size()/2], "p95_ms": sorted[mini(sorted.size()-1, ceili(sorted.size()*0.95)-1)],
		"p99_ms": sorted[mini(sorted.size()-1, ceili(sorted.size()*0.99)-1)], "max_ms": sorted.back()}

func mouse_events(point: Vector2, click := true) -> Array[InputEvent]:
	var result: Array[InputEvent] = []
	var motion := InputEventMouseMotion.new()
	motion.position = point
	motion.global_position = point
	result.append(motion)
	if click:
		for down in [true, false]:
			var event := InputEventMouseButton.new()
			event.position = point
			event.global_position = point
			event.button_index = MOUSE_BUTTON_LEFT
			event.button_mask = MOUSE_BUTTON_MASK_LEFT if down else 0
			event.pressed = down
			result.append(event)
	return result

func key_event(code: Key, unicode := 0, ctrl := false) -> InputEventKey:
	var event := InputEventKey.new()
	event.keycode = code
	event.unicode = unicode
	event.ctrl_pressed = ctrl
	event.pressed = true
	return event

func dispatch(name: String) -> void:
	for event in events[name]: viewport.push_input(event, true)

func drive(workload: String, frame: int) -> bool:
	match workload:
		"ui_disabled", "idle": return false
		"clock_1hz":
			if frame % 60 != 0: return false
			game.state.tick()
		"clock_update": game.state.tick()
		"clock_direct":
			game.state.elapsed_seconds += 1
			var minutes: int = 17 * 60 + 40 + game.state.elapsed_seconds
			game.state.model.View.Clock = "%02d:%02d" % [(minutes / 60) % 24, minutes % 60]
			game.state.derive()
			ui.set_element_text("#clock", game.state.model.View.Clock)
		"vitals_10hz", "vitals_update", "vitals_direct":
			var step := 6 if workload == "vitals_10hz" else 1
			if frame % step != 0: return false
			var value := 40 + (frame / step) % 59
			game.state.model.Player.Health = value
			game.state.model.Player.Stamina = 139 - value
			if workload == "vitals_direct":
				game.state.derive()
				ui.set_element_text("#health", str(value))
				ui.set_element_style("#health-bar", "width", "%d%%" % value)
				ui.set_element_text("#stamina", str(139 - value))
				ui.set_element_style("#stamina-bar", "width", "%d%%" % (139 - value))
			else:
				game.state.publish()
		"redundant_signal": game.state.changed.emit()
		"hover": dispatch("hover-a" if frame % 2 else "hover-b")
		"inventory_sort":
			if frame % 6 != 0: return false
			dispatch("sort")
		"settings_typing":
			if frame % 6 != 0: return false
			var edit := frame / 6
			if edit % 20 == 0: dispatch("select-all")
			dispatch("type")
		"settings_slider":
			if frame % 6 != 0: return false
			dispatch("left" if (frame / 6) % 2 == 0 else "right")
		"settings_toggle":
			if frame % 6 != 0: return false
			dispatch("close" if game.settings_open else "open")
	return true

func prepare(workload: String) -> void:
	game.get_node("Clock").stop()
	ui.set_process(false)
	ui.close_dialog("#settings")
	game.settings_open = false
	game.world_actions = 0
	ui.set_focus("")
	ui.clear_pointer()
	ui.show()
	var fresh = game.CampState.new()
	game.state.model.clear()
	game.state.model.merge(fresh.model)
	game.state.elapsed_seconds = 0
	require(ui.load_files("res://ui/camp.html") == OK, "load original sample")
	ui.bind_state(game.state.model, game, game.state.changed)
	ui.update_document(0)
	events = {
		"open": mouse_events(ui.query_bounds("#settings-button").get_center()),
		"sort": mouse_events(ui.query_bounds("#sort").get_center()),
		"hover-a": mouse_events(ui.query_bounds("#sort").get_center(), false),
		"hover-b": mouse_events(ui.query_bounds("#forage").get_center(), false),
		"select-all": [key_event(KEY_A, 97, true)],
		"type": [key_event(KEY_X, 120)],
		"left": [key_event(KEY_LEFT)], "right": [key_event(KEY_RIGHT)]
	}
	if workload.begins_with("settings_"):
		game.open_settings("")
		ui.update_document(0)
		events["close"] = mouse_events(ui.query_bounds("#close-settings").get_center())
		if workload == "settings_slider":
			ui.set_focus("#volume")
		elif workload == "settings_toggle":
			game.close_settings("")
		ui.update_document(0)
	if workload == "ui_disabled": ui.hide()
	ui.set_process(auto_update and workload != "ui_disabled")
	await tree.process_frame
	await tree.process_frame
	pending = false
	refreshes = 0

func verify_case(workload: String, last_frame: int) -> void:
	require(ui.get_missing_assets().is_empty(), workload + " assets")
	require(game.world_actions == 0, workload + " does not accidentally trigger gameplay")
	match workload:
		"idle", "ui_disabled": require(refreshes == 0, workload + " performs no binding refresh")
		"clock_1hz", "clock_update", "clock_direct":
			require(ui.query_text("#clock") == game.state.model.View.Clock and game.state.elapsed_seconds > 0, workload + " clock visibly changes")
		"vitals_10hz", "vitals_update", "vitals_direct":
			require(ui.query_text("#health") == str(game.state.model.Player.Health), workload + " text reaches screen")
			require(is_equal_approx(ui.query_bounds("#health-bar").size.x, ui.query_bounds(".vital .meter").size.x * game.state.model.Player.Health / 100.0), workload + " bar matches data")
		"inventory_sort":
			require(ui.query_all_ids("#inventory > .item")[0] == "item-" + game.state.model.Items[0].Id, "sort reaches rendered rows")
			require(game.state.model.View.Message == "Satchel order reversed.", "native sort handler executed")
		"hover": require(ui.has_element("#sort:hover" if last_frame % 2 else "#forage:hover"), "native pointer changes hover target")
		"settings_typing": require(ui.get_element_value("#player-name") == "x".repeat(((last_frame / 6) % 20) + 1) and ui.query_text("#player-label") == game.state.model.Player.Name, "native typing reaches control, model and HUD")
		"settings_slider": require(game.state.model.Settings.Volume == (64 if (last_frame / 6) % 2 == 0 else 65), "slider edits typed model")
		"settings_toggle": require(game.settings_open == (((last_frame / 6) % 2) == 0), "modal toggle executes")

func measure(workload: String) -> Dictionary:
	require(workload in CASES or workload in ["clock_direct", "vitals_direct"], "known workload: " + workload)
	await prepare(workload)
	var api: Array[float] = []
	var core: Array[float] = []
	var wall: Array[float] = []
	var changed_api: Array[float] = []
	var changed_wall: Array[float] = []
	var binding: Array[float] = []
	var viewport_cpu: Array[float] = []
	var viewport_gpu: Array[float] = []
	for values in [api, core, wall, binding, viewport_cpu, viewport_gpu, changed_api, changed_wall]: values.resize(frames)
	var changed_count := 0
	var start_static := 0
	var end_static := 0
	var render_rid := viewport.get_viewport_rid()
	for tick in warmups + frames:
		# Measured before the next frame's drives; no screenshot/geometry reads
		# in this section. Array growth and metric reads stay outside API timing.
		var start := Time.get_ticks_usec()
		var changing := drive(workload, tick)
		var binding_ms := 0.0
		if not auto_update:
			if pending:
				var bind_start := Time.get_ticks_usec()
				ui.flush_bindings()
				binding_ms = (Time.get_ticks_usec() - bind_start) / 1000.0
				pending = false
			if workload != "ui_disabled": ui.update_document(1.0/60.0)
		var api_ms := (Time.get_ticks_usec() - start) / 1000.0
		var core_ms := 0.0 if workload == "ui_disabled" else ui.get_last_update_ms()
		await tree.process_frame
		var wall_ms := (Time.get_ticks_usec() - start) / 1000.0
		if tick == warmups: start_static = OS.get_static_memory_usage()
		if tick >= warmups:
			var index := tick - warmups
			api[index] = api_ms
			core[index] = core_ms
			wall[index] = wall_ms
			binding[index] = binding_ms
			viewport_cpu[index] = RenderingServer.viewport_get_measured_render_time_cpu(render_rid)
			viewport_gpu[index] = RenderingServer.viewport_get_measured_render_time_gpu(render_rid)
			if changing:
				changed_api[changed_count] = api_ms
				changed_wall[changed_count] = wall_ms
				changed_count += 1
	end_static = OS.get_static_memory_usage()
	changed_api.resize(changed_count)
	changed_wall.resize(changed_count)
	verify_case(workload, warmups + frames - 1)
	var result := {"workload": workload, "frames": frames, "warmups": warmups,
		"api_cpu": stats(api), "core_cpu": stats(core), "whole_frame": stats(wall),
		"changed_api_cpu": stats(changed_api), "changed_whole_frame": stats(changed_wall),
		"binding_cpu": stats(binding), "viewport_render_cpu": stats(viewport_cpu), "viewport_render_gpu": stats(viewport_gpu),
		"binding_refreshes": refreshes, "elements": ui.count_elements("*"),
		"weva_draw_commands": ui.get_draw_count(), "weva_triangles": ui.get_triangle_count(),
		"canvas_draw_calls": RenderingServer.viewport_get_render_info(render_rid, RenderingServer.VIEWPORT_RENDER_INFO_TYPE_CANVAS, RenderingServer.VIEWPORT_RENDER_INFO_DRAW_CALLS_IN_FRAME),
		"godot_static_memory_change_bytes": end_static - start_static if start_static > 0 else null,
		"texture_memory_bytes": RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TEXTURE_MEM_USED)}
	if capture_enabled:
		await RenderingServer.frame_post_draw
		require(viewport.get_texture().get_image().save_png(output.path_join(workload + ".png")) == OK, "capture " + workload)
	print("FRONTIER_PERF ", JSON.stringify(result))
	return result

func run(scene: Control) -> void:
	game = scene
	ui = game.ui
	tree = game.get_tree()
	viewport = game.get_viewport()
	game.get_node("Clock").stop()
	viewport.notify_mouse_entered()
	Engine.max_fps = 0
	OS.low_processor_usage_mode = false
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	RenderingServer.viewport_set_measure_render_time(viewport.get_viewport_rid(), true)
	game.state.changed.connect(func(): pending = true)
	ui.bindings_refreshed.connect(func(_count): refreshes += 1)
	output = OS.get_environment("WEVA_FRONTIER_PERF_OUT")
	auto_update = OS.get_environment("WEVA_FRONTIER_AUTO") == "1"
	capture_enabled = OS.get_environment("WEVA_FRONTIER_CAPTURE") == "1"
	if OS.has_environment("WEVA_FRONTIER_FRAMES"): frames = int(OS.get_environment("WEVA_FRONTIER_FRAMES"))
	if OS.has_environment("WEVA_FRONTIER_WARMUPS"): warmups = int(OS.get_environment("WEVA_FRONTIER_WARMUPS"))
	var workloads: Array = CASES.duplicate()
	if OS.has_environment("WEVA_FRONTIER_CASES"): workloads = Array(OS.get_environment("WEVA_FRONTIER_CASES").split(","))
	if OS.get_environment("WEVA_FRONTIER_REVERSE") == "1": workloads.reverse()
	await tree.process_frame
	await tree.process_frame
	var results: Array[Dictionary] = []
	for workload in workloads: results.append(await measure(workload))
	var report := {"passed": not failed, "auto_update": auto_update,
		"engine": Engine.get_version_info(), "debug_build": OS.is_debug_build(),
		"renderer": RenderingServer.get_current_rendering_method(), "adapter": RenderingServer.get_video_adapter_name(),
		"viewport": [int(ui.size.x), int(ui.size.y)], "simulated_dt": 1.0/60.0,
		"results": results}
	if not output.is_empty(): FileAccess.open(output.path_join("results.json"), FileAccess.WRITE).store_string(JSON.stringify(report, "\t"))
	print("FRONTIER_PERF_COMPLETE ", not failed)
	tree.quit(1 if failed else 0)
