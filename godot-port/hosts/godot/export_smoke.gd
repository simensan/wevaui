extends Node

var failures := 0
var checks := 0

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func _ready() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--weva-capture="):
			_capture_example(argument.trim_prefix("--weva-capture="))
			return
	if "--weva-example-test" in OS.get_cmdline_user_args():
		get_tree().change_scene_to_file.call_deferred("res://example_test.tscn")
		return
	var packed := "--packed" in OS.get_cmdline_user_args()
	var mode := "pack" if packed else "project"
	var native_debug := "--native-debug" in OS.get_cmdline_user_args()
	var native_release := "--native-release" in OS.get_cmdline_user_args()
	if native_debug or native_release:
		mode = "native-debug" if native_debug else "native-release"
		check(OS.has_feature("template"), "running an exported template")
		check(not OS.has_feature("editor"), "running without editor features")
		check(OS.is_debug_build() == native_debug, "template matches the requested export configuration")
	if packed:
		check(not FileAccess.file_exists("res://art/pixel.png"), "raw PNG is absent from the exported pack")
		check(ResourceLoader.exists("res://art/pixel.png"), "imported PNG is present in the exported pack")
	var doc := WevaDocument.new()
	doc.document_size = Vector2(160, 120)
	doc.use_engine_font = false
	doc.set_base_path("res://")
	doc.css = FileAccess.get_file_as_string("res://ui.css")
	doc.html = FileAccess.get_file_as_string("res://ui.html")
	add_child(doc)
	doc.update_document(0)
	check(doc.has_element("#icon"), "HTML is available")
	check(doc.query_bounds("#panel").size == Vector2(40, 30), "CSS is available")
	check(doc.get_missing_assets().is_empty(), "images load: " + str(doc.get_missing_assets()))
	check(doc.query_bounds("#icon").size == Vector2(8, 6), "PNG retains its intrinsic dimensions")
	check(doc.query_bounds("#vector").size == Vector2(9, 7), "Godot-imported SVG has intrinsic dimensions")
	check(doc.get_draw_count() >= 2, "image and background produce draws")
	doc.css = "#panel{width:20px;height:10px}"
	doc.update_document(0)
	check(doc.query_bounds("#panel").size == Vector2(20, 10), "CSS replacement works after launch")
	doc.append_html("body", '<img src="art/missing.png">')
	doc.update_document(0)
	check(doc.get_missing_assets() == PackedStringArray(["res://art/missing.png"]), "missing artwork remains diagnosable")
	doc.free()
	print("godot export smoke (%s): %d checks, %d failures" % [mode, checks, failures])
	get_tree().quit(1 if failures else 0)

func _capture_example(path: String) -> void:
	var scene: Node = load("res://example_test.tscn").instantiate()
	add_child(scene)
	var canvas: SubViewport = scene.get_node("Viewport")
	canvas.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	for frame in 3:
		await get_tree().process_frame
	await RenderingServer.frame_post_draw
	var image := canvas.get_texture().get_image()
	if image.get_size() != Vector2i(640, 720) or image.save_png(path) != OK:
		printerr("FAIL  exported example capture")
		get_tree().quit(1)
		return
	print("godot export render: 640x720")
	get_tree().quit()
