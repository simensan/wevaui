extends Node

func _ready() -> void:
	var mode := OS.get_environment("WEVA_WARMUP_MODE")
	var steps := 0
	var warmup_started := Time.get_ticks_usec()
	if mode == "partial":
		ClassDB.class_call_static("WevaDocument", "warmup_fonts_step")
	elif mode == "full":
		while not ClassDB.class_call_static("WevaDocument", "warmup_fonts_step"):
			steps += 1
			if steps > 64:
				push_error("Font warmup did not complete")
				get_tree().quit(1)
				return
			await get_tree().process_frame
	var warmup_elapsed := Time.get_ticks_usec() - warmup_started
	var construction_started := Time.get_ticks_usec()
	var doc := WevaDocument.new()
	doc.paused = true
	doc.document_size = Vector2(1280, 720)
	doc.html = '<div id="latin">Camp supply: café ★ ⚔ 🛡 😀</div><div id="jp">日本語の文字</div><div id="zh">中文測試 简体中文</div><div id="kr">한국어 문자</div><div id="other">Живой текст العربية á</div>'
	doc.css = 'html,body{margin:0;background:#15202b;color:white}div{font-size:36px;line-height:70px;padding-left:20px}'
	add_child(doc)
	doc.update_document(0)
	var construction_elapsed := Time.get_ticks_usec() - construction_started
	var failures := 0
	for selector in ["#latin", "#jp", "#zh", "#kr", "#other"]:
		if doc.query_bounds(selector).size.y < 70:
			failures += 1
	if mode != "cold" and not ClassDB.class_call_static("WevaDocument", "warmup_fonts_step"):
		failures += 1
	await get_tree().process_frame
	await RenderingServer.frame_post_draw
	var output := OS.get_environment("WEVA_WARMUP_IMAGE")
	if get_viewport().get_texture().get_image().save_png(output) != OK:
		failures += 1
	print("Font warmup render: ", failures, " failures")
	print("FONT_WARMUP_TIMING ", JSON.stringify({"mode":mode,
		"warmup_elapsed_ms":float(warmup_elapsed)/1000.0,
		"construction_cpu_ms":float(construction_elapsed)/1000.0}))
	get_tree().quit(1 if failures else 0)
