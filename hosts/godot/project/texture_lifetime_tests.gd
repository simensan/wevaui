extends Node

var checks := 0
var failures := 0

func check(ok: bool, description: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", description)

func _ready() -> void:
	var render := DisplayServer.get_name() != "headless"
	var view := SubViewport.new()
	view.size = Vector2i(48, 48)
	view.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(view)
	for blend in ["", "mix-blend-mode:multiply;"]:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.use_engine_font = false
		view.add_child(doc)
		for step in range(24):
			if step % 4 == 0:
				doc.html = "<div></div>"
			doc.document_size = Vector2(40 + step % 2, 40)
			var red := 32 + step * 8
			doc.css = "body{margin:0;background:white}div{width:40px;height:40px;%sbackground:linear-gradient(rgb(%d,64,128),rgb(%d,64,128))}" % [blend, red, red]
			doc.update_document(0)
			check(doc.get_stats()["textures"] == 1, "one published replacement texture at step %d" % step)
			if render:
				await get_tree().process_frame
				await RenderingServer.frame_post_draw
				var actual := view.get_texture().get_image().get_pixel(20, 20)
				check(absf(actual.r - red / 255.0) < 0.025 and absf(actual.g - 64 / 255.0) < 0.025 and absf(actual.b - 128 / 255.0) < 0.025,
					"replacement pixels at step %d" % step)
		doc.free()
	view.free()
	print("godot texture lifetime: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
