extends SceneTree

# Export live sample state and the actual fallback font for browser cross-checks.
func _initialize() -> void:
	call_deferred("run")

func snapshot(doc: WevaDocument) -> Dictionary:
	var nodes := {}
	for id in doc.query_all_ids("[id]"):
		var selector := "#" + id
		var attrs := {}
		for attribute in ["class", "style", "src", "disabled"]:
			attrs[attribute] = doc.get_element_attribute(selector, attribute) if doc.has_element_attribute(selector, attribute) else null
		var rect := doc.query_bounds(selector)
		var state := {"attributes": attrs, "bounds": [rect.position.x, rect.position.y, rect.size.x, rect.size.y]}
		if doc.count_elements(selector + " > *") == 0:
			state["text"] = doc.get_element_text(selector)
		nodes[id] = state
	return nodes

func run() -> void:
	var output := OS.get_environment("WEVA_RENDER_EXPORT_OUT")
	if output.is_empty():
		quit(1)
		return
	root.size = Vector2i(1280, 720)
	var sample = load("res://samples/western_survival/survival.tscn").instantiate()
	root.add_child(sample)
	sample.set_process(false)
	var doc: WevaDocument = sample.get("ui")
	doc.set_process(false)
	doc.interactive = false
	await process_frame
	await process_frame
	var font: Font = doc.get_theme_font("font")
	var bytes = font.get("data")
	if not bytes is PackedByteArray or bytes.is_empty():
		printerr("FAIL cannot export the actual Godot fallback font")
		quit(1)
		return
	var font_file := FileAccess.open(output + ".ttf", FileAccess.WRITE)
	if font_file == null:
		printerr("FAIL cannot write exported font")
		quit(1)
		return
	font_file.store_buffer(bytes)
	font_file.close()
	var data := {"html": doc.html, "css": doc.css, "items": sample.get("items"),
		"recipes": sample.get_script().get_script_constant_map()["RECIPES"], "states": {}}
	for state in ["hud", "inventory"]:
		if state == "hud": sample.call("close_inventory")
		else: sample.call("open_inventory")
		doc.update_document(0)
		data.states[state] = snapshot(doc)
	data["selection_states"] = []
	for index in 12:
		sample.call("select_inventory_item", "item-%d" % index)
		doc.update_document(0)
		data.selection_states.append(snapshot(doc))
	var file := FileAccess.open(output, FileAccess.WRITE)
	if file == null:
		printerr("FAIL cannot write exported fixture")
		quit(1)
		return
	file.store_string(JSON.stringify(data, "\t"))
	file.close()
	quit()
