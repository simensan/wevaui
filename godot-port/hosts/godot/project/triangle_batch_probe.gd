extends SceneTree

# Used by check_triangle_batching.py with the same library in both modes.
func _initialize() -> void:
	call_deferred("run_probe")

func run_probe() -> void:
	var output := OS.get_environment("WEVA_BATCH_OUTPUT")
	if output.is_empty() or DisplayServer.get_name() == "headless":
		printerr("triangle batching requires a renderer and WEVA_BATCH_OUTPUT")
		quit(1)
		return
	DirAccess.make_dir_recursive_absolute(output)
	var viewport := SubViewport.new()
	viewport.size = Vector2i(640, 480)
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var doc := WevaDocument.new()
	doc.interactive = false
	doc.use_engine_font = false
	doc.document_size = Vector2(640, 480)
	viewport.add_child(doc)
	var cases: Array[Dictionary] = [
		{"name": "overlap", "html": "<div class='a'></div><div class='b'></div><div class='c'>Texture boundary</div><div class='a second'></div>",
		 "css": "div{position:absolute;width:180px;height:120px;border-radius:22px;border:3px solid #a9d3eb}.a{left:25px;top:25px;background:rgba(230,40,70,.45)}.b{left:100px;top:80px;background:rgba(20,220,120,.6)}.c{left:160px;top:130px;color:white;background:linear-gradient(30deg,#461974,#1587ce)}.second{left:220px;top:185px}"},
		{"name": "clips", "html": "<section><div class='a'>Clipped text and gradient</div><div class='b'></div><div class='c'></div></section>",
		 "css": "section{position:absolute;left:40px;top:40px;width:350px;height:230px;border-radius:45px;overflow:hidden;background:#1b2038}section div{position:absolute;width:240px;height:140px;border-radius:28px}.a{left:-20px;top:30px;background:linear-gradient(90deg,#b32170,#42b7db);color:white;transform:rotate(8deg)}.b{left:100px;top:100px;background:rgba(40,210,140,.5);box-shadow:0 2px 9px #000}.c{left:220px;top:-40px;background:#e8b940}"},
		{"name": "backdrop", "html": "<div class='a'></div><div class='b'></div><div class='glass'></div><div class='front'></div><div class='glass second'></div>",
		 "css": "div{position:absolute;width:230px;height:130px;border-radius:24px}.a{left:15px;top:20px;background:#ca3068}.b{left:140px;top:100px;background:#269ac0}.glass{left:70px;top:60px;background:rgba(255,255,255,.15);backdrop-filter:blur(6px) saturate(.7)}.front{left:110px;top:135px;background:rgba(40,220,90,.5)}.second{left:170px;top:160px}"},
		{"name": "sdf", "sdf": true, "html": "<div class='a'></div><p>Material boundary</p><div class='b'></div><div class='c'></div>",
		 "css": "div{position:absolute;width:130px;height:90px;border-radius:20px}.a{left:20px;top:20px;background:#de3355}.b{left:80px;top:70px;background:#25b491}.c{left:160px;top:130px;background:#426ee3}p{position:absolute;left:30px;top:40px;color:white}"}
	]
	var tiles := PackedStringArray()
	for i in 1400:
		tiles.append("<i style='left:%dpx;top:%dpx'></i>" % [(i%40)*16,(i/40)*13])
	cases.append({"name": "capacity", "html": "".join(tiles),
		"css": "i{position:absolute;width:15px;height:12px;border-radius:4px;background:rgba(55,145,230,.7);border:1px solid #8cd3ec}"})
	cases.append({"name": "empty", "html": "", "css": ""})
	for test in cases:
		doc.use_sdf_rects = test.get("sdf", false)
		doc.css = "body{margin:0;background:#151923;font-size:18px}" + test.css
		doc.html = test.html
		doc.update_document()
		for frame in 3:
			doc.queue_redraw()
			await process_frame
			await RenderingServer.frame_post_draw
		var image := viewport.get_texture().get_image()
		if image == null or image.is_empty() or image.save_png(output.path_join(test.name + ".png")) != OK:
			printerr("FAIL triangle batching image: ", test.name)
			quit(1)
			return
		print("triangle batching case: ", test.name)
	print("triangle batching: %d images" % cases.size())
	quit()
