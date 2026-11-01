extends SceneTree
# Opt-in real-render cache-pressure check. Give each run a new absolute
# WEVA_IMAGE_CACHE_OUT directory; PNG fixtures and captures remain as evidence.
var checks := 0
var failures := 0
var output := ""

func check(ok: bool, description: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", description)

func _initialize() -> void:
	output = OS.get_environment("WEVA_IMAGE_CACHE_OUT")
	if not output.is_absolute_path() or DirAccess.dir_exists_absolute(output):
		printerr("WEVA_IMAGE_CACHE_OUT must be a new absolute directory")
		quit(2)
		return
	call_deferred("run")

func run() -> void:
	check(DirAccess.make_dir_recursive_absolute(output) == OK, "create owned output directory")
	var red := Image.create(256, 256, false, Image.FORMAT_RGBA8)
	red.fill(Color.RED)
	check(red.save_png(output.path_join("red.png")) == OK, "save independent PNG fixture")
	var original := output.path_join("live.png")
	check(red.save_png(original) == OK, "save initial live source")
	var view := SubViewport.new()
	view.size = Vector2i(128, 64)
	view.transparent_bg = true
	view.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(view)
	var doc := WevaDocument.new()
	doc.paused = true
	doc.document_size = Vector2(128, 64)
	view.add_child(doc)
	doc.html = "<body><img id='changing' src='%s'><img id='pinned' src='%s'></body>" % [original.xml_escape(true), output.path_join("red.png").xml_escape(true)]
	doc.css = "body{margin:0}img{position:absolute;top:0;width:32px;height:32px}#changing{left:0}#pinned{left:40px}"
	doc.update_document(0)
	await process_frame
	await RenderingServer.frame_post_draw
	var before := view.get_texture().get_image()
	check(before.get_pixel(8, 8).r > 0.9 and before.get_pixel(8, 8).b < 0.1, "initial source renders red")
	check(before.save_png(output.path_join("before.png")) == OK, "save initial render")
	# Distinct, small PNG files avoid importer and long-path assumptions.
	# 320 decoded 256x256 RGBA sources exceed the 64 MiB cache target.
	for iteration in range(1, 321):
		var source := output.path_join("source-%03d.png" % iteration)
		check(red.save_png(source) == OK, "save unique source fixture")
		check(doc.set_element_attribute("#changing", "src", source), "swap source under cache pressure")
		doc.update_document(0)
		if iteration % 32 == 0:
			await process_frame
			await RenderingServer.frame_post_draw
	var blue := Image.create(256, 256, false, Image.FORMAT_RGBA8)
	blue.fill(Color.BLUE)
	check(blue.save_png(original) == OK, "replace owned source bytes after eviction")
	check(doc.set_element_attribute("#changing", "src", original), "return to evicted URL")
	doc.update_document(0)
	await process_frame
	await RenderingServer.frame_post_draw
	var after := view.get_texture().get_image()
	var changed := after.get_pixel(8, 8)
	var pinned := after.get_pixel(48, 8)
	check(changed.b > 0.9 and changed.r < 0.1 and changed.a > 0.9, "fresh blue pixels reach the real renderer")
	check(pinned.r > 0.9 and pinned.b < 0.1 and pinned.a > 0.9, "pinned source remains red")
	check(after.save_png(output.path_join("after.png")) == OK, "save final render")
	doc.free()
	view.free()
	await process_frame
	await RenderingServer.frame_post_draw
	var receipt := FileAccess.open(output.path_join("result.json"), FileAccess.WRITE)
	check(receipt != null, "open render result")
	var result := {"checks": checks, "failures": failures, "source_swaps": 320,
		"changed_pixel": [changed.r, changed.g, changed.b, changed.a],
		"pinned_pixel": [pinned.r, pinned.g, pinned.b, pinned.a]}
	if receipt:
		receipt.store_string(JSON.stringify(result, "  "))
		receipt.close()
	print("godot image-cache pressure: ", checks, " checks, ", failures, " failures")
	quit(1 if failures else 0)
