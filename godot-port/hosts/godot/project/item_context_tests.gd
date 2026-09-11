extends Node

var checks := 0
var failures := 0

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func viewport() -> SubViewport:
	var view := SubViewport.new()
	view.size = Vector2i(240, 180)
	view.transparent_bg = true
	view.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(view)
	return view

func document(view: SubViewport, engine_font: bool, contents: bool, control: bool) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.document_size = Vector2(240, 180)
	doc.use_engine_font = engine_font
	doc.paused = true
	doc.interactive = false
	doc.css = "#r{display:flex;width:200px;flex-direction:column;grid-template-columns:200px;gap:5px;align-items:start}" + \
		"#a{width:100px;margin:7px 0 11px;background:#125}" + \
		"#child{height:10px;margin:17px 0 13px;background:#f80}#next{height:9px;width:100px;background:#0f0}" + \
		"#contents{display:contents}"
	if control:
		doc.css += "#a{display:flow-root}"
	doc.html = '<div id="r">' + ('<div id="contents">' if contents else '') + \
		'<div id="a"><div id="child"></div></div>' + ('</div>' if contents else '') + '<div id="next"></div></div>'
	view.add_child(doc)
	doc.update_document(0)
	return doc

func _ready() -> void:
	var live_view := viewport()
	var control_view := viewport()
	var render := DisplayServer.get_name() != "headless"
	for engine_font in [false, true]:
		for contents in [false, true]:
			var live := document(live_view, engine_font, contents, false)
			for display in ["flex", "grid", "inline-flex", "inline-grid"]:
				for fixed in [false, true]:
					for margin in [17, 31, 17]:
						var control := document(control_view, engine_font, contents, true)
						for doc in [live, control]:
							check(doc.set_element_style("#r", "display", display), "mutate container display")
							check(doc.set_element_style("#a", "height", "20px" if fixed else "auto"), "mutate item height")
							check(doc.set_element_style("#child", "margin-top", "%dpx" % margin), "mutate child margin")
							doc.update_document(0)
						var height: int = 20 if fixed else margin + 10 + 13
						var r := live.query_bounds("#r")
						var a := live.query_bounds("#a")
						var label := "%s/fixed=%s/contents=%s/margin=%d/engine=%s" % [display, fixed, contents, margin, engine_font]
						check(is_equal_approx(a.position.y-r.position.y, 7), label + " item position")
						check(is_equal_approx(a.size.y, height), label + " item height")
						check(is_equal_approx(live.query_bounds("#child").position.y-a.position.y, margin), label + " child position")
						check(is_equal_approx(live.query_bounds("#next").position.y-r.position.y, 7+height+11+5), label + " following position")
						check(is_equal_approx(r.size.y, 7+height+11+5+9), label + " container height")
						check(a.is_equal_approx(control.query_bounds("#a")), label + " independent flow-root control")
						if render:
							await get_tree().process_frame
							await get_tree().process_frame
							await RenderingServer.frame_post_draw
							check(live_view.get_texture().get_image().get_data() == control_view.get_texture().get_image().get_data(), label + " control pixels")
						control.free()
			live.free()
		for display in ["flex", "grid", "inline-flex", "inline-grid"]:
			var doc := document(live_view, engine_font, false, false)
			doc.css = "#r{display:%s;width:200px;flex-direction:column;grid-template-columns:200px;gap:5px;align-items:start}#a{width:100px}#child{float:left;width:30px;height:37px}#next{height:9px;width:100px}" % display
			doc.update_document(0)
			check(is_equal_approx(doc.query_bounds("#a").size.y, 37), display + " contains float height")
			check(is_equal_approx(doc.query_bounds("#next").position.y-doc.query_bounds("#r").position.y, 42), display + " next follows contained float")
			doc.css = "#outer{display:flow-root;width:400px}#float{float:left;width:350px;height:60px}#r{display:%s;width:120px;flex-direction:column;grid-template-columns:120px}#a{width:120px;font-size:16px;line-height:20px}" % display
			doc.html = '<div id="outer"><div id="float"></div><div id="r"><div id="a">aa aa aa aa aa</div></div></div>'
			doc.update_document(0)
			check(is_equal_approx(doc.query_bounds("#a").size.y, 20), display + " text excludes outside float")
			doc.free()
	print("godot item context: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
