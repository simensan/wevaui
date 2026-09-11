extends RefCounted
## Run the release export with -- --lifecycle.
## Separates scene loading, first construction, hidden preparation and reuse.

var failed := false
var output := ""
var cycles := 200
var seconds := 120.0
var soak_frames := 0
var soak_without_ui := false
# Ownership isolation: each soak workload can be disabled, and the soak plus
# teardown can repeat so retained objects classify as bounded or accumulating.
var soak_settings := true
var soak_sort := true
var soak_names := true
var soak_rounds := 1
var unicode_names := false
var name_updates := 0
var extent := Vector2i(1920, 1080)
var packed: PackedScene
var world: Node3D
var results: Dictionary = {}
var tree: SceneTree
var root: Window

func require(ok: bool, label: String) -> void:
	if not ok:
		failed = true
		printerr("FAIL frontier lifecycle: " + label)

func stats(values: Array[float]) -> Dictionary:
	if values.is_empty(): return {"samples": 0}
	var sorted := values.duplicate()
	sorted.sort()
	var total := 0.0
	for value in values: total += value
	return {"samples": values.size(), "mean_ms": total / values.size(),
		"median_ms": sorted[sorted.size() / 2], "p95_ms": sorted[mini(sorted.size() - 1, ceili(sorted.size() * 0.95) - 1)],
		"max_ms": sorted[-1]}

func marker(phase: String, index := 0) -> void:
	print("FRONTIER_MEMORY ", JSON.stringify({"phase": phase, "index": index,
		"time_usec": Time.get_ticks_usec(), "static_bytes": OS.get_static_memory_usage(),
		"nodes": Performance.get_monitor(Performance.OBJECT_NODE_COUNT),
		"objects": Performance.get_monitor(Performance.OBJECT_COUNT),
		"texture_bytes": RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TEXTURE_MEM_USED)}))

func soak_stats(buckets: PackedInt64Array, count: int, total: float, maximum: float, overflow: int) -> Dictionary:
	# Fixed 10us buckets: the observer must not retain one Variant per frame in
	# the test whose purpose is to establish a memory bound. Quantiles are upper
	# bounds within one bucket; overflow uses the actual maximum conservatively.
	var quantiles := {}
	for entry in [["median_ms", 0.5], ["p95_ms", 0.95]]:
		var target := maxi(1, ceili(count * entry[1]))
		var cumulative := 0
		for bucket in buckets.size():
			cumulative += buckets[bucket]
			if cumulative >= target:
				quantiles[entry[0]] = maximum if bucket == buckets.size() - 1 else bucket * 0.01
				break
	return {"samples": count, "mean_ms": total / maxi(1, count), "max_ms": maximum,
		"median_ms": quantiles.get("median_ms", 0.0), "p95_ms": quantiles.get("p95_ms", 0.0),
		"quantile_resolution_ms": 0.01, "quantile_limit_ms": (buckets.size() - 1) * 0.01,
		"quantile_overflow_samples": overflow}

func create_game(hidden: bool) -> Control:
	var game: Control = packed.instantiate()
	game.visible = not hidden
	root.add_child(game)
	game.get_node("Clock").stop()
	if world != null:
		game.get_node("World").hide()
		game.get_node("Shade").hide()
	game.ui.update_document(0)
	require(game.ui.get_missing_assets().is_empty(), "construction loads assets")
	return game

func check_game(game: Control) -> void:
	require(game.ui.query_text("#health") == str(game.state.model.Player.Health), "latest model reaches HUD")
	require(game.ui.query_text("#player-label") == game.state.model.Player.Name, "latest player name reaches HUD")
	require(game.ui.query_all_ids("#inventory > .item")[0] == "item-" + game.state.model.Items[0].Id, "row order reaches layout")
	require(game.ui.size == Vector2(extent), "actual UI resolution")
	require(game.world_actions == 0, "lifecycle creates no gameplay clicks")

func update_name(game: Control, index: int) -> void:
	update_state_name(game.state, index)

func update_state_name(state: RefCounted, index: int) -> void:
	# Unique suffixes prevent whole-string reuse; fixed mixed-script text keeps
	# glyph vocabulary bounded while exercising fallback and mark shaping.
	state.model.Player.Name = ("á😀b".repeat(60) + " Ж😀б العربية %d" % index) if unicode_names else "Traveler %d" % (index % 100)
	name_updates += 1

func drop_game(game: Control) -> void:
	var old_ui: WeakRef = weakref(game.ui)
	var state = game.state
	game.queue_free()
	await tree.process_frame
	await tree.process_frame
	require(old_ui.get_ref() == null, "native UI destroyed")
	require(state.changed.get_connections().is_empty(), "destroyed view disconnects game state")
	# Emitting after teardown must not invoke a queued/dead binding target.
	state.changed.emit()

func capture(name: String) -> void:
	if output.is_empty(): return
	await RenderingServer.frame_post_draw
	require(root.get_texture().get_image().save_png(output.path_join(name + ".png")) == OK, "capture " + name)

func run(scene_tree: SceneTree) -> void:
	tree = scene_tree
	root = tree.root
	# Boot's _ready is still inside the root's child setup notification.
	await tree.process_frame
	output = OS.get_environment("WEVA_FRONTIER_PERF_OUT")
	if OS.has_environment("WEVA_FRONTIER_CYCLES"): cycles = int(OS.get_environment("WEVA_FRONTIER_CYCLES"))
	if OS.has_environment("WEVA_FRONTIER_SECONDS"): seconds = float(OS.get_environment("WEVA_FRONTIER_SECONDS"))
	if OS.has_environment("WEVA_FRONTIER_SOAK_FRAMES"): soak_frames = int(OS.get_environment("WEVA_FRONTIER_SOAK_FRAMES"))
	soak_without_ui = OS.get_environment("WEVA_FRONTIER_SOAK_WITHOUT_UI") == "1"
	soak_settings = OS.get_environment("WEVA_FRONTIER_SOAK_SETTINGS") != "0"
	soak_sort = OS.get_environment("WEVA_FRONTIER_SOAK_SORT") != "0"
	soak_names = OS.get_environment("WEVA_FRONTIER_SOAK_NAMES") != "0"
	if OS.has_environment("WEVA_FRONTIER_SOAK_ROUNDS"): soak_rounds = maxi(1, int(OS.get_environment("WEVA_FRONTIER_SOAK_ROUNDS")))
	unicode_names = OS.get_environment("WEVA_FRONTIER_UNICODE") == "1"
	if unicode_names:
		require(TextServerManager.get_primary_interface().string_to_upper("i", "tr") == "İ", "Unicode lifecycle has ICU data")
	if OS.has_environment("WEVA_FRONTIER_RESOLUTION"):
		var dimensions := OS.get_environment("WEVA_FRONTIER_RESOLUTION").split("x")
		extent = Vector2i(int(dimensions[0]), int(dimensions[1]))
	root.size = extent
	root.content_scale_size = extent
	Engine.max_fps = 0
	OS.low_processor_usage_mode = false
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	if OS.get_environment("WEVA_FRONTIER_WORLD") == "3d":
		world = load("res://tests/load_world.gd").new()
		root.add_child(world)
	await tree.process_frame
	await tree.process_frame
	var start := Time.get_ticks_usec()
	packed = load("res://main.tscn")
	results["scene_resource_load_ms"] = (Time.get_ticks_usec() - start) / 1000.0
	marker("before_document")
	var first_hidden := OS.get_environment("WEVA_FRONTIER_FIRST_HIDDEN") == "1"
	start = Time.get_ticks_usec()
	var game := create_game(first_hidden)
	results["first_document_cpu_ms"] = (Time.get_ticks_usec() - start) / 1000.0
	var show_start := Time.get_ticks_usec()
	game.show()
	game.ui.update_document(0)
	results["first_show_cpu_ms"] = (Time.get_ticks_usec() - show_start) / 1000.0
	await RenderingServer.frame_post_draw
	results["first_show_through_draw_ms"] = (Time.get_ticks_usec() - show_start) / 1000.0
	results["first_document_through_draw_ms"] = (Time.get_ticks_usec() - start) / 1000.0
	check_game(game)
	await capture("cold")
	await drop_game(game)
	# Settle the completed teardown coroutine before sampling object counts.
	await tree.process_frame
	await tree.process_frame
	marker("after_first_teardown")
	start = Time.get_ticks_usec()
	game = create_game(true)
	results["hidden_prepare_cpu_ms"] = (Time.get_ticks_usec() - start) / 1000.0
	await tree.process_frame
	start = Time.get_ticks_usec()
	game.show()
	game.ui.update_document(0)
	results["prepared_first_show_cpu_ms"] = (Time.get_ticks_usec() - start) / 1000.0
	await RenderingServer.frame_post_draw
	results["prepared_first_show_through_draw_ms"] = (Time.get_ticks_usec() - start) / 1000.0
	await capture("prepared")
	var reuse: Array[float] = []
	var reuse_draw: Array[float] = []
	for index in cycles + 20:
		game.ui.hide()
		game.ui.set_process(false)
		game.state.model.Player.Health = 40 + index % 60
		if unicode_names: update_name(game, name_updates)
		game.state.publish()
		await tree.process_frame
		start = Time.get_ticks_usec()
		game.ui.show()
		game.ui.set_process(true)
		game.ui.flush_bindings()
		game.ui.update_document(0)
		var cpu := (Time.get_ticks_usec() - start) / 1000.0
		await RenderingServer.frame_post_draw
		if index >= 20:
			reuse.append(cpu)
			reuse_draw.append((Time.get_ticks_usec() - start) / 1000.0)
		check_game(game)
	results["reuse_with_hidden_data_update_cpu"] = stats(reuse)
	results["reuse_through_draw"] = stats(reuse_draw)
	await drop_game(game)
	# Settle the completed teardown coroutine before sampling object counts.
	await tree.process_frame
	await tree.process_frame
	marker("before_recreate")
	var recreate: Array[float] = []
	for index in cycles + 20:
		start = Time.get_ticks_usec()
		game = create_game(false)
		if unicode_names:
			update_name(game, name_updates)
			game.state.publish()
			game.ui.flush_bindings()
			game.ui.update_document(0)
			check_game(game)
		var cpu := (Time.get_ticks_usec() - start) / 1000.0
		if index >= 20: recreate.append(cpu)
		game.state.sort_items()
		# Leave a deferred refresh pending at teardown to exercise ownership.
		await drop_game(game)
		if index % 20 == 0: marker("recreate", index)
	results["recreate_cpu"] = stats(recreate)
	# Settle the completed teardown coroutine before sampling object counts.
	await tree.process_frame
	await tree.process_frame
	marker("after_recreate")
	for round_index in soak_rounds:
		await soak_round(round_index)
	marker("final_teardown")
	var report := {"passed": not failed, "debug_build": OS.is_debug_build(), "engine": Engine.get_version_info(),
		"first_hidden": first_hidden,
		"soak_frame_target": soak_frames, "soak_without_ui": soak_without_ui,
		"soak_settings": soak_settings, "soak_sort": soak_sort, "soak_names": soak_names, "soak_rounds": soak_rounds,
		"unicode_names": unicode_names, "name_updates": name_updates,
		"renderer": RenderingServer.get_current_rendering_method(), "resolution": [extent.x, extent.y],
		"world": "3d" if world != null else "static", "cycles": cycles, "results": results}
	if not output.is_empty(): FileAccess.open(output.path_join("lifecycle.json"), FileAccess.WRITE).store_string(JSON.stringify(report, "\t"))
	print("FRONTIER_LIFECYCLE_COMPLETE ", not failed)
	tree.quit(1 if failed else 0)

func soak_round(round_index: int) -> void:
	var start := 0
	var game := create_game(false)
	# Both arms warm identical UI resources first. The baseline then releases
	# the document while retaining the same model and deterministic scene drive.
	var soak_state = game.state
	if soak_without_ui:
		await drop_game(game)
		game = null
	var interval_buckets := PackedInt64Array()
	interval_buckets.resize(10001)
	interval_buckets.fill(0)
	var interval_total := 0.0
	var interval_max := 0.0
	var interval_overflow := 0
	var frame := 0
	var soak_start := Time.get_ticks_usec()
	marker("soak_start")
	while (frame < soak_frames if soak_frames > 0 else Time.get_ticks_usec() - soak_start < seconds * 1000000):
		start = Time.get_ticks_usec()
		if world != null: world.set_step(frame)
		if frame % 6 == 0:
			soak_state.model.Player.Health = 40 + (frame / 6) % 60
			if soak_names: update_state_name(soak_state, name_updates)
			soak_state.publish()
		if soak_sort and frame % 60 == 0: soak_state.sort_items()
		if soak_settings and game != null and frame % 120 == 0:
			if game.settings_open: game.close_settings("")
			else: game.open_settings("")
		await tree.process_frame
		var interval := (Time.get_ticks_usec() - start) / 1000.0
		interval_total += interval
		interval_max = maxf(interval_max, interval)
		var bucket := ceili(interval * 100.0)
		if bucket >= interval_buckets.size(): interval_overflow += 1
		interval_buckets[mini(bucket, interval_buckets.size() - 1)] += 1
		if frame % 600 == 0:
			if game != null: check_game(game)
			else: require(soak_state.changed.get_connections().is_empty(), "baseline has no UI binding listeners")
			marker("soak", frame)
		frame += 1
	results["soak_seconds"] = (Time.get_ticks_usec() - soak_start) / 1000000.0
	results["soak_frames"] = soak_stats(interval_buckets, frame, interval_total, interval_max, interval_overflow)
	if game != null: check_game(game)
	await capture("soak" if round_index == 0 else "soak-round-%d" % round_index)
	if game != null: await drop_game(game)
	# The soak driver owns the model independently of the view. Release it and
	# let completed coroutine/renderer cleanup settle before the final sample.
	var old_soak_state: WeakRef = weakref(soak_state)
	soak_state = null
	await tree.process_frame
	await tree.process_frame
	require(old_soak_state.get_ref() == null, "soak state released after teardown")
	old_soak_state = null
	if soak_rounds > 1: marker("round_teardown", round_index)
