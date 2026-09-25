extends Node2D
func _ready() -> void:
	get_window().size = Vector2i(256, 128)
	var doc := WevaDocument.new()
	doc.document_size = Vector2(256, 128)
	doc.html = '<div style="width:100px;height:80px;background:red"></div>'
	add_child(doc)
	doc.set_process(false)
	doc.update_document(0)
	var overlay := Polygon2D.new()
	overlay.polygon = PackedVector2Array([Vector2(20,20),Vector2(40,20),Vector2(40,40),Vector2(20,40)])
	overlay.color = Color.BLUE
	doc.add_child(overlay)
	var light := PointLight2D.new()
	var light_image := Image.create(4, 4, false, Image.FORMAT_RGBA8)
	light_image.fill(Color.WHITE)
	light.texture = ImageTexture.create_from_image(light_image)
	light.position = Vector2(50,40)
	light.texture_scale = 32
	light.blend_mode = Light2D.BLEND_MODE_SUB
	light.enabled = false
	add_child(light)
	for phase in ["initial", "self-modulate", "redraw", "parent-modulate", "transform", "clip", "hide", "show", "empty", "reload", "light-included", "light-excluded", "visibility-excluded", "sdf-on", "sdf-off", "backdrop", "plain", "reparent", "material", "material-change", "material-clear", "texture-nearest", "texture-linear", "instance-uniform", "instance-change", "instance-default", "instance-reload", "inherited-material", "inherited-change", "inherited-clear", "own-material"]:
		if phase == "self-modulate": doc.self_modulate = Color(0.5,1,1,0.5)
		if phase == "redraw": doc.queue_redraw()
		if phase == "parent-modulate": doc.modulate = Color(1,0.5,1,0.5)
		if phase == "transform":
			doc.modulate = Color.WHITE
			doc.self_modulate = Color.WHITE
			doc.position = Vector2(15, 10)
			doc.scale = Vector2(0.8, 0.8)
		if phase == "clip":
			doc.clip_contents = true
			doc.size = Vector2(50, 40)
		if phase == "hide": doc.hide()
		if phase == "show": doc.show()
		if phase == "empty":
			doc.html = ""
			doc.update_document(0)
		if phase == "reload":
			doc.clip_contents = false
			doc.html = '<div style="width:100px;height:80px;background:red"></div>'
			doc.update_document(0)
		if phase == "light-included":
			light.enabled = true
			doc.light_mask = 1
		if phase == "light-excluded": doc.light_mask = 2
		if phase == "visibility-excluded":
			light.enabled = false
			doc.visibility_layer = 2
			get_viewport().canvas_cull_mask = 1
		if phase == "sdf-on":
			doc.visibility_layer = 1
			doc.use_sdf_rects = true
			doc.html = '<div style="width:100px;height:80px;border-radius:14px;background:red"></div>'
			doc.update_document(0)
		if phase == "sdf-off":
			doc.use_sdf_rects = false
			doc.update_document(0)
		if phase == "backdrop":
			doc.html = '<div style="width:100px;height:80px;background:red"><div style="width:60px;height:40px;backdrop-filter:blur(2px);background:#ffffff44"></div></div>'
			doc.update_document(0)
		if phase == "plain":
			doc.html = '<div style="width:100px;height:80px;background:green"></div>'
			doc.update_document(0)
		if phase == "reparent":
			var parent := Node2D.new()
			parent.position = Vector2(10,5)
			add_child(parent)
			doc.reparent(parent, false)
		if phase == "material":
			var shader := Shader.new()
			shader.code = "shader_type canvas_item; void fragment(){ COLOR.rgb = vec3(COLOR.g, COLOR.r, COLOR.b); }"
			var material := ShaderMaterial.new()
			material.shader = shader
			doc.material = material
		if phase == "material-change":
			var shader := Shader.new()
			shader.code = "shader_type canvas_item; void fragment(){ COLOR.rgb *= vec3(0.3,0.5,0.7); }"
			var material := ShaderMaterial.new()
			material.shader = shader
			doc.material = material
		if phase == "material-clear": doc.material = null
		if phase == "texture-nearest":
			var texture_image := Image.create(2, 2, false, Image.FORMAT_RGBA8)
			texture_image.fill(Color.RED)
			texture_image.set_pixel(1, 0, Color.GREEN)
			texture_image.set_pixel(0, 1, Color.BLUE)
			texture_image.set_pixel(1, 1, Color.WHITE)
			var path := OS.get_environment("WEVA_RETAINED_TEST_OUT") + "-texture.png"
			texture_image.save_png(path)
			doc.base_path = path.get_base_dir()
			doc.html = '<img src="%s" style="width:100px;height:80px">' % path.get_file()
			doc.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
			doc.update_document(0)
		if phase == "texture-linear": doc.texture_filter = CanvasItem.TEXTURE_FILTER_LINEAR
		if phase == "instance-uniform":
			var shader := Shader.new()
			shader.code = "shader_type canvas_item; instance uniform vec4 tint = vec4(1.0); void fragment(){ COLOR *= tint; }"
			var material := ShaderMaterial.new()
			material.shader = shader
			doc.material = material
			doc.set_instance_shader_parameter("tint", Color(0.2,0.5,0.7,1))
		if phase == "instance-change": doc.set_instance_shader_parameter("tint", Color(0.7,0.2,0.5,1))
		if phase == "instance-default": doc.set_instance_shader_parameter("tint", null)
		if phase == "instance-reload":
			doc.set_instance_shader_parameter("tint", Color(0.3,0.7,0.5,1))
			doc.html = '<div style="width:100px;height:80px;background:red"></div><div style="width:80px;height:20px;background:blue"></div>'
			doc.update_document(0)
		if phase == "inherited-material":
			doc.get_parent().material = doc.material
			doc.material = null
			doc.use_parent_material = true
		if phase == "inherited-change":
			var shader := Shader.new()
			shader.code = "shader_type canvas_item; instance uniform vec4 tint = vec4(1.0); void fragment(){ COLOR *= tint * vec4(0.5,0.5,0.5,1.0); }"
			var material := ShaderMaterial.new()
			material.shader = shader
			doc.get_parent().material = material
		if phase == "inherited-clear": doc.get_parent().material = null
		if phase == "own-material": doc.use_parent_material = false
		await get_tree().process_frame
		await RenderingServer.frame_post_draw
		get_viewport().get_texture().get_image().save_png(OS.get_environment("WEVA_RETAINED_TEST_OUT") + "-" + phase + ".png")
	doc.free()
	get_tree().quit()
