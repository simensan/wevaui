extends SceneTree

var _pre_draw_ticks := 0
var _post_draw_ticks := 0
var _pre_draw_frame := -1
var _post_draw_frame := -1

func _record_pre_draw() -> void:
	_pre_draw_ticks = Time.get_ticks_usec()
	_pre_draw_frame = Engine.get_process_frames()

func _record_post_draw() -> void:
	_post_draw_ticks = Time.get_ticks_usec()
	_post_draw_frame = Engine.get_process_frames()

# The actual gallery, including automatic document processing and drawing.
# Run with --script res://layout_stress_probe.gd --resolution 1520x800.
# WEVA_GALLERY_PROBE_FRAMES controls measured frames (default 300 after 240 warmups).
# Use --fixed-fps 60 to compare the same animation interval without a frame cap.
# WEVA_GALLERY_PROBE_COLD_BUILDS instead measures opening fresh documents;
# each build includes parsing, engine fonts and host texture preparation.
# WEVA_GALLERY_PROBE_TRACE writes per-frame CSV after measurement. Its frame
# IDs match WEVA_GODOT_DRAW_LOG; process_frame observes the completed frame.
# GPU timing is opt-in with WEVA_GALLERY_PROBE_GPU_TIMING=1 because collecting
# timestamps adds work to the renderer. Async render callbacks produce NaN
# phase durations when they do not bracket the same completed frame.
func _initialize() -> void:
	call_deferred("run_probe")

func stats(values: Array[float]) -> String:
	var ordered := values.duplicate()
	ordered.sort()
	var total := 0.0
	for v in values:
		total += v
	return "mean %.3f, median %.3f, p95 %.3f, max %.3f ms" % [total/values.size(),ordered[ordered.size()/2],ordered[mini(ordered.size()-1,int(ordered.size()*0.95))],ordered.back()]

func run_probe() -> void:
	Engine.max_fps = 0
	if DisplayServer.get_name() != "headless":
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var gallery = load("res://gallery.tscn").instantiate()
	root.add_child(gallery)
	var names: Array = gallery.get("_names")
	var index := names.find("layout-stress")
	if index < 0:
		printerr("FAIL layout-stress missing from gallery")
		quit(1)
		return
	var cold_count := int(OS.get_environment("WEVA_GALLERY_PROBE_COLD_BUILDS"))
	if cold_count > 0:
		var builds: Array[float] = []
		for i in cold_count:
			gallery.call("_show",index)
			var cold_doc: WevaDocument = gallery.get("_doc")
			cold_doc.interactive = false
			builds.append(gallery.get("_build_ms").back())
			await process_frame
		print("layout-stress cold: %d fresh documents, %s" % [cold_count,DisplayServer.get_name()])
		print("first layout-stress build: %.3f ms" % builds[0])
		print("cold gallery build: ",stats(builds))
		quit()
		return
	gallery.call("_show",index)
	var doc: WevaDocument = gallery.get("_doc")
	doc.interactive = false
	gallery.set_process_unhandled_input(false)
	var viewport_rid := root.get_viewport_rid()
	var gpu_timing := OS.get_environment("WEVA_GALLERY_PROBE_GPU_TIMING") == "1"
	RenderingServer.viewport_set_measure_render_time(viewport_rid,gpu_timing)
	var frames := 300
	if OS.has_environment("WEVA_GALLERY_PROBE_FRAMES"):
		frames = maxi(1,int(OS.get_environment("WEVA_GALLERY_PROBE_FRAMES")))
	var wall: Array[float] = []
	var updates: Array[float] = []
	var process_monitor := 0.0
	var render_cpu: Array[float] = []
	var render_gpu: Array[float] = []
	var draw_calls := 0.0
	var frame_ids: Array[int] = []
	var trace_path := OS.get_environment("WEVA_GALLERY_PROBE_TRACE")
	var until_pre_draw: Array[float] = []
	var drawing: Array[float] = []
	var after_draw: Array[float] = []
	if not trace_path.is_empty():
		RenderingServer.frame_pre_draw.connect(_record_pre_draw)
		RenderingServer.frame_post_draw.connect(_record_post_draw)
	var previous := Time.get_ticks_usec()
	for i in 240+frames:
		await process_frame
		var now := Time.get_ticks_usec()
		if i >= 240:
			if not trace_path.is_empty():
				var completed_frame := Engine.get_process_frames()-1
				frame_ids.append(completed_frame)
				var same_frame := _pre_draw_frame == completed_frame and _post_draw_frame == completed_frame
				until_pre_draw.append((_pre_draw_ticks-previous)/1000.0 if same_frame else NAN)
				drawing.append((_post_draw_ticks-_pre_draw_ticks)/1000.0 if same_frame else NAN)
				after_draw.append((now-_post_draw_ticks)/1000.0 if same_frame else NAN)
			wall.append((now-previous)/1000.0)
			updates.append(doc.get_last_update_ms())
			process_monitor = Performance.get_monitor(Performance.TIME_PROCESS)*1000.0
			if gpu_timing:
				render_cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(viewport_rid))
				render_gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(viewport_rid))
			draw_calls += Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME)
		previous = now
	print("layout-stress gallery: %d frames, %s, %d core draws / %d triangles / %.1f GPU draw calls" % [frames,DisplayServer.get_name(),doc.get_draw_count(),doc.get_triangle_count(),draw_calls/frames])
	print("wall frame: ",stats(wall))
	print("last Weva update: ",stats(updates))
	# Godot publishes process_max once per second, not each frame's cost.
	print("Godot process monitor: %.3f ms (latest rolling maximum)" % process_monitor)
	if gpu_timing:
		print("viewport render CPU: ",stats(render_cpu))
		print("viewport render GPU: ",stats(render_gpu))
	else:
		print("viewport timing disabled (WEVA_GALLERY_PROBE_GPU_TIMING=1 enables it)")
	if not trace_path.is_empty():
		var trace := FileAccess.open(trace_path, FileAccess.WRITE)
		if trace == null:
			printerr("FAIL cannot write frame trace: ", trace_path)
			quit(1)
			return
		trace.store_line("frame,wall_ms,update_ms,render_cpu_ms,render_gpu_ms,before_render_ms,render_ms,after_render_ms")
		for i in frames:
			trace.store_line("%d,%.6f,%.6f,%s,%s,%.6f,%.6f,%.6f" % [frame_ids[i],wall[i],updates[i],str(render_cpu[i]) if gpu_timing else "",str(render_gpu[i]) if gpu_timing else "",until_pre_draw[i],drawing[i],after_draw[i]])
		trace.close()
	quit()
