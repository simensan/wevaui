extends SceneTree

var sample: Control
var doc: WevaDocument

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	root.size = Vector2i(1280,720)
	sample = load("res://samples/western_survival/survival.tscn").instantiate()
	root.add_child(sample)
	doc = sample.get("ui")
	sample.set_process(false)
	doc.set_process(false)
	doc.interactive = false
	await process_frame
	await process_frame
	await measure("idle HUD",false,false)
	await measure("sprinting HUD",true,false)
	await measure("open inventory",false,true)
	quit()

func measure(label: String, sprint: bool, menu: bool) -> void:
	sample.set("sprinting",sprint)
	sample.set("stamina",100.0)
	if menu: sample.call("open_inventory")
	else: sample.call("close_inventory")
	doc.update_document(0)
	var times: Array[float] = []
	var changed: Array[float] = []
	for i in 360:
		await process_frame
		var before: int = sample.get("_shown_stamina")
		var start := Time.get_ticks_usec()
		sample.call("_process",1.0/60.0)
		doc.update_document(1.0/60.0)
		var ms := (Time.get_ticks_usec()-start)/1000.0
		if i >= 60:
			times.append(ms)
			if before != int(sample.get("_shown_stamina")): changed.append(ms)
	times.sort()
	changed.sort()
	print("%s | controller + document CPU ms median %.4f p95 %.4f max %.4f | changed frames %d p95 %.4f | UI draws %d triangles %d" % [label,times[150],times[285],times[-1],changed.size(),0.0 if changed.is_empty() else changed[int(changed.size()*0.95)],doc.get_draw_count(),doc.get_triangle_count()])
