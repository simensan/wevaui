extends SceneTree

# Cache enabled/disabled must produce identical pixels after every mutation.
func _initialize() -> void:
	call_deferred("run_probe")

func run_probe() -> void:
	var output := OS.get_environment("WEVA_BATCH_OUTPUT")
	if output.is_empty() or DisplayServer.get_name() == "headless":
		printerr("packed draw probe requires a renderer and WEVA_BATCH_OUTPUT")
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
	var native_child := ColorRect.new()
	native_child.position = Vector2(130, 110)
	native_child.size = Vector2(90, 55)
	native_child.color = Color(0.8, 0.2, 0.1, 0.6)
	doc.add_child(native_child)
	var html := "<div id='a'></div><div id='b'>Retained text</div><div id='c'></div>"
	doc.html = html
	doc.css = "body{margin:0;background:#151923;font-size:18px}div{width:250px;height:100px;border-radius:20px;background:#acf}#b{background:linear-gradient(90deg,#13a,#5bd);color:white}#c{background:#c47}"
	var shader := Shader.new()
	shader.code = "shader_type canvas_item; instance uniform vec4 tint = vec4(1.0); void fragment(){COLOR *= tint;}"
	var material := ShaderMaterial.new()
	material.shader = shader
	var names := ["initial", "color", "remove_fill", "gradient", "grow", "engine_font",
		"font_spacing", "resize", "self_modulate", "shader", "instance_uniform", "child_behind",
		"sdf", "backdrop", "backdrop_removed", "stub_font", "hidden", "shown", "empty", "reload"]
	for step in names.size():
		match step:
			1: doc.set_element_style("#a", "background", "#bfa")
			2: doc.set_element_style("#a", "background", "none")
			3: doc.set_element_style("#b", "background", "linear-gradient(90deg,#f31,#dab)")
			4: doc.set_element_style("#a", "height", "130px")
			5: doc.use_engine_font = true
			6:
				var font := FontVariation.new()
				font.base_font = ThemeDB.fallback_font
				font.set_spacing(TextServer.SPACING_GLYPH, 3)
				doc.add_theme_font_override("font", font)
			7: doc.document_size = Vector2(210, 260)
			8: doc.self_modulate = Color(0.5, 0.8, 0.7, 0.65)
			9: doc.material = material
			10: doc.set_instance_shader_parameter("tint", Vector4(0.6, 1.0, 0.7, 1.0))
			11: native_child.show_behind_parent = true
			12: doc.use_sdf_rects = true
			13: doc.set_element_style("#c", "backdrop-filter", "blur(5px) saturate(.7)")
			14: doc.set_element_style("#c", "backdrop-filter", "none")
			15: doc.use_engine_font = false
			16: doc.hide()
			17: doc.show()
			18: doc.html = ""
			19: doc.html = html
		doc.update_document()
		# First draw consumes the mutation; subsequent draws must hit the cache.
		for frame in 3:
			doc.queue_redraw()
			await process_frame
			await RenderingServer.frame_post_draw
		var image := viewport.get_texture().get_image()
		if image == null or image.is_empty() or image.save_png(output.path_join(names[step] + ".png")) != OK:
			printerr("FAIL packed draw image: ", names[step])
			quit(1)
			return
		print("packed draw case: ", names[step])
	print("packed draws: %d images" % names.size())
	quit()
