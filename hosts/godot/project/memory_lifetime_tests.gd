extends SceneTree

var checks := 0
var failures := 0
var mode := "cycle"
var view: SubViewport
var doc: WevaDocument
const HTML = "<body><div class=panel>Survival camp<input value='Supplies'><span>Food 12 / Water 8</span></div></body>"
const CSS = "body{margin:0;background:#18202a;color:white;font-size:14px}.panel{width:150px;height:90px;border-radius:8px;background:linear-gradient(#28394a,#18222f);box-shadow:0 2px 4px #000;padding:8px}input{width:120px}span{display:block}"

func _initialize() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--mode="): mode = arg.trim_prefix("--mode=")
	call_deferred("run")

func check(ok: bool, description: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", description)

func create_document() -> WevaDocument:
	var node := WevaDocument.new()
	node.paused = true
	node.document_size = Vector2(180, 120)
	view.add_child(node)
	node.html = HTML
	node.css = CSS
	node.update_document(0)
	return node

func sample(iteration: int) -> Dictionary:
	var row := {"mode": mode, "iteration": iteration,
		"objects": Performance.get_monitor(Performance.OBJECT_COUNT),
		"resources": Performance.get_monitor(Performance.OBJECT_RESOURCE_COUNT),
		"nodes": Performance.get_monitor(Performance.OBJECT_NODE_COUNT),
		"static_bytes": Performance.get_monitor(Performance.MEMORY_STATIC),
		"texture_bytes": Performance.get_monitor(Performance.RENDER_TEXTURE_MEM_USED),
		"buffer_bytes": Performance.get_monitor(Performance.RENDER_BUFFER_MEM_USED)}
	print("MEMORY_SAMPLE ", JSON.stringify(row))
	return row

func run() -> void:
	check(mode in ["idle", "reload", "cycle", "theme"], "known mode")
	view = SubViewport.new()
	view.size = Vector2i(180, 120)
	view.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(view)
	if mode != "cycle": doc = create_document()
	var baseline: Dictionary
	for iteration in range(401):
		if mode == "cycle": doc = create_document()
		elif mode == "reload":
			doc.html = HTML
			doc.css = CSS
		elif mode == "theme":
			var skin := Theme.new()
			var font := FontVariation.new()
			font.base_font = ThemeDB.fallback_font
			font.variation_embolden = float(iteration % 2)
			skin.default_font = font
			doc.theme = skin
		doc.update_document(0)
		await process_frame
		await RenderingServer.frame_post_draw
		if iteration % 40 == 0:
			check(doc.query_text("span") == "Food 12 / Water 8", "text remains usable")
			check(doc.get_stats()["textures"] > 0, "text and background textures published")
			var image := view.get_texture().get_image()
			var panel := image.get_pixel(150, 80)
			check(panel.a > 0.9 and panel.b > panel.r + 0.025, "blue gradient panel rendered")
			if iteration == 400 and not OS.get_environment("WEVA_MEMORY_OUT").is_empty():
				check(image.save_png(OS.get_environment("WEVA_MEMORY_OUT").path_join("godot-" + mode + ".png")) == OK, "render evidence saved")
		if mode == "cycle":
			doc.free()
			doc = null
		await process_frame
		await RenderingServer.frame_post_draw
		if iteration % 40 == 0:
			var row := sample(iteration)
			if iteration == 80: baseline = row
			if iteration > 80:
				check(row.nodes == baseline.nodes, "node count returns to plateau")
				check(row.objects <= baseline.objects + 4, "object count remains bounded")
				check(row.resources <= baseline.resources + 2, "resource count remains bounded")
				check(row.static_bytes <= baseline.static_bytes + 256 * 1024, "engine allocations remain within warm budget")
	if doc != null: doc.free()
	view.free()
	await process_frame
	await RenderingServer.frame_post_draw
	print("godot memory lifetime: %d checks, %d failures" % [checks, failures])
	quit(1 if failures else 0)
