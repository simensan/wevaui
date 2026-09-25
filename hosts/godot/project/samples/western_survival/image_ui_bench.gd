extends SceneTree

# Retained image-heavy game UI, using this sample's imported item artwork.
# CPU includes the public API write plus update; construction and GPU work are
# outside the timer. Run without builds/tests or profiling logs in flight.
const ICONS := ["revolver","hatchet","canteen","beans","bandage","campfire","wood","stone","cloth","rope","ammo","knife"]
const CSS := """
html,body{margin:0;background:#111810;color:#dfd3b9;font-family:sans-serif;font-size:14px}
#panel{position:absolute;left:36px;top:28px;width:720px;padding:24px;background:#232b22;border:1px solid #807350}
h1{font-size:22px;margin:0 0 18px}#items{height:548px;overflow-y:scroll;display:grid;grid-template-columns:repeat(6,100px);grid-auto-rows:84px;gap:8px}
.slot{position:relative;background:#343b2c;border:1px solid #6b7051;padding:7px}
img{display:block;width:62px;height:62px;object-fit:contain}span{position:absolute;right:6px;bottom:6px}
"""
var doc: WevaDocument

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	root.size = Vector2i(1280,720)
	doc = WevaDocument.new()
	doc.base_path = "res://samples/western_survival"
	doc.css = CSS
	var markup := '<main id="panel"><h1>Field stores</h1><section id="items">'
	for i in 96:
		markup += '<div class="slot"><img src="assets/%s.svg" alt=""><span>%d</span></div>' % [ICONS[i % ICONS.size()],i + 1]
	doc.html = markup + '</section></main>'
	root.add_child(doc)
	doc.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	doc.set_process(false)
	doc.interactive = false
	doc.update_document(0)
	assert(doc.get_element_scroll_max("#items").y > 440)
	assert(doc.get_missing_assets().is_empty())
	var report := {"warmup_frames":60,"measured_frames":300,"items":96,"results":[]}
	var output := OS.get_environment("WEVA_IMAGE_BENCH_OUT")
	for workload in ["scroll","fade"]:
		doc.set_element_scroll("#items",Vector2.ZERO)
		doc.set_element_style("#panel","opacity","1")
		doc.update_document(0)
		var times: Array[float] = []
		for i in 360:
			await process_frame
			var start := Time.get_ticks_usec()
			if workload == "scroll":
				doc.set_element_scroll("#items",Vector2(0,20 + (i % 60) * 7))
			else:
				doc.set_element_style("#panel","opacity",str(0.5 + (i % 60)/120.0))
			doc.update_document(1.0/60.0)
			var ms := (Time.get_ticks_usec()-start)/1000.0
			if i >= 60: times.append(ms)
		if workload == "scroll": assert(doc.get_element_scroll("#items").y == 433)
		times.sort()
		var result := {"workload":workload,"median_ms":times[150],"p95_ms":times[285],"max_ms":times[-1],"draws":doc.get_draw_count(),"triangles":doc.get_triangle_count()}
		report.results.append(result)
		print("IMAGE_UI ",JSON.stringify(result))
		if not output.is_empty():
			await RenderingServer.frame_post_draw
			await RenderingServer.frame_post_draw
			assert(root.get_texture().get_image().save_png(output.path_join(workload+".png")) == OK)
	if not output.is_empty():
		var file := FileAccess.open(output.path_join("results.json"),FileAccess.WRITE)
		assert(file != null)
		file.store_string(JSON.stringify(report,"\t"))
	print("IMAGE_UI_COMPLETE")
	quit()
