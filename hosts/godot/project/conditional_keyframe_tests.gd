extends Node

func _ready() -> void:
	var oracle = JSON.parse_string(FileAccess.get_file_as_string("res://conditional_keyframe_cases.json"))
	var failures := 0
	var checks := 0
	for target: String in ["#a", "#panel"]:
		var panel := WevaDocument.new()
		panel.paused = true
		panel.document_size = Vector2(400, 300)
		panel.html = '<div id="panel"><div id="a">HUD</div></div>'
		panel.css = '#a{width:20px;animation:grow 1s linear infinite}@keyframes grow{from{width:100px}to{width:300px}}'
		add_child(panel)
		panel.update_document(0.5)
		checks += 1
		if not is_equal_approx(panel.query_bounds("#a").size.x, 200.0):
			failures += 1
			printerr("FAIL panel animation before hiding: ", target)
		panel.set_element_style(target, "display", "none")
		panel.update_document(0)
		panel.update_document(0.25)
		panel.set_element_style(target, "display", "block")
		panel.update_document(0)
		checks += 1
		if not is_equal_approx(panel.query_bounds("#a").size.x, 100.0):
			failures += 1
			printerr("FAIL panel animation restart: ", target)
		panel.update_document(0.25)
		checks += 1
		if not is_equal_approx(panel.query_bounds("#a").size.x, 150.0):
			failures += 1
			printerr("FAIL panel animation after reopening: ", target)
		panel.free()
	var composition = JSON.parse_string(FileAccess.get_file_as_string("res://animation_composition_cases.json"))
	for row in composition.rows:
		var composed := WevaDocument.new()
		composed.paused = true
		composed.document_size = Vector2(400, 300)
		composed.html = '<div id="a">HUD</div>'
		composed.css = row.css
		add_child(composed)
		for index in 4:
			if index == 3:
				composed.set_element_style("#a", "animation-name", "steady")
			composed.update_document([0.25, 0.35, 0.4, 0.0][index])
			checks += 1
			if absf(composed.query_bounds("#a").size.x - float(row.widths[index])) > 0.02:
				failures += 1
				printerr("FAIL animation composition: ", row.shared, " delayed ", row.delayed, " step ", index)
		composed.free()
	if DisplayServer.get_name() != "headless":
		var covered := WevaDocument.new()
		covered.paused = true
		covered.document_size = Vector2(400, 300)
		covered.html = '<div id="a">HUD</div>'
		covered.css = '#a{animation:pulse 2s linear infinite,cover 2s linear infinite}@keyframes pulse{from{width:100px}to{width:200px}}@keyframes cover{from{width:300px}to{width:300px}}'
		var draws := [0]
		covered.draw.connect(func(): draws[0] += 1)
		add_child(covered)
		await get_tree().create_timer(0.1).timeout
		var initial_draws: int = draws[0]
		checks += 1
		if initial_draws == 0:
			failures += 1
			printerr("FAIL covered animation initial draw was not observed")
		for frame in 10:
			covered.update_document(0.01)
			await get_tree().process_frame
		await get_tree().process_frame
		checks += 2
		if draws[0] != initial_draws:
			failures += 1
			printerr("FAIL covered animation requested redundant redraws")
		if not is_equal_approx(covered.query_bounds("#a").size.x, 300.0):
			failures += 1
			printerr("FAIL covered animation changed width")
		covered.free()
	var endpoints = JSON.parse_string(FileAccess.get_file_as_string("res://animation_endpoint_cases.json"))
	for row in endpoints.rows:
		var endpoint := WevaDocument.new()
		endpoint.paused = true
		endpoint.document_size = Vector2(400, 300)
		endpoint.html = '<div id="a">HUD</div>'
		endpoint.css = row.css
		add_child(endpoint)
		for index in 2:
			if index == 1:
				endpoint.set_element_style("#a", "animation-play-state", "running")
			endpoint.update_document(float(row.times[index]))
			checks += 1
			if not is_equal_approx(endpoint.query_bounds("#a").size.x, float(row.widths[index])):
				failures += 1
				printerr("FAIL animation endpoint: ", row.direction, " duration ", row.duration,
					" count ", row.count, " step ", index)
		endpoint.free()
	var cyclic := WevaDocument.new()
	cyclic.paused = true
	cyclic.document_size = Vector2(400, 300)
	cyclic.html = '<div id="a">HUD</div>'
	cyclic.css = '#a{width:20px;animation-name:steady,steady,pulse;animation-duration:2s,4s;animation-timing-function:linear;animation-iteration-count:infinite}@keyframes steady{from{opacity:.5}to{opacity:.5}}@keyframes pulse{from{width:100px}to{width:200px}}'
	add_child(cyclic)
	cyclic.update_document(0.5)
	checks += 1
	if not is_equal_approx(cyclic.query_bounds("#a").size.x, 125.0):
		failures += 1
		printerr("FAIL repeating animation duration list")
	cyclic.free()
	for properties: String in ["opacity,height,width", "width,width,height,width"]:
		var transition := WevaDocument.new()
		transition.paused = true
		transition.document_size = Vector2(400, 300)
		transition.html = '<div id="a">HUD</div>'
		transition.css = '#a{width:100px;transition-property:' + properties + ';transition-duration:2s,4s;transition-timing-function:linear}'
		add_child(transition)
		transition.update_document(0)
		transition.set_element_style("#a", "width", "200px")
		transition.update_document(0)
		transition.update_document(0.5)
		checks += 1
		if not is_equal_approx(transition.query_bounds("#a").size.x, 125.0 if properties.begins_with("opacity") else 112.5):
			failures += 1
			printerr("FAIL repeating transition duration list: ", properties)
		transition.free()
	for row in oracle.rows:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.document_size = Vector2(400, 300)
		doc.html = '<div id="a">HUD</div>'
		doc.css = '#a{width:20px;animation:pulse 1s linear infinite}' + row.css
		add_child(doc)
		for index in 3:
			doc.document_size = Vector2(800 if index == 1 else 400, 300)
			doc.update_document(0)
			var actual: float = doc.query_bounds("#a").size.x
			checks += 1
			if not is_equal_approx(actual, float(row.widths[index])):
				failures += 1
				printerr("FAIL conditional keyframe: ", row.name, " step ", index,
					" expected ", row.widths[index], " got ", actual)
		doc.free()
	for background_animation: bool in [false, true]:
		var lifecycle := WevaDocument.new()
		lifecycle.paused = true
		lifecycle.document_size = Vector2(400, 300)
		lifecycle.html = '<div id="a">HUD</div>'
		lifecycle.css = '#a{width:20px;animation:pulse 2s linear infinite' + (',steady 2s linear infinite' if background_animation else '') + '}@keyframes steady{from{opacity:.5}to{opacity:.5}}@media(min-width:600px){@keyframes pulse{from{width:100px}to{width:200px}}}'
		add_child(lifecycle)
		lifecycle.update_document(5)
		for step in [[800, 0.0, 100.0], [800, 0.5, 125.0], [400, 2.0, 20.0], [800, 0.0, 100.0]]:
			lifecycle.document_size = Vector2(step[0], 300)
			lifecycle.update_document(step[1])
			checks += 1
			if not is_equal_approx(lifecycle.query_bounds("#a").size.x, float(step[2])):
				failures += 1
				printerr("FAIL conditional animation clock: ", step)
		lifecycle.free()
	var list_oracle = JSON.parse_string(FileAccess.get_file_as_string("res://animation_clock_cases.json"))
	var list := WevaDocument.new()
	list.paused = true
	list.document_size = Vector2(400, 300)
	list.html = '<div id="a">HUD</div>'
	list.css = '#a{width:20px;animation:pulse 2s linear infinite,steady 2s linear infinite}@keyframes pulse{from{width:100px}to{width:200px}}@keyframes replacement{from{width:100px}to{width:200px}}@keyframes steady{from{opacity:.5}to{opacity:.5}}'
	add_child(list)
	list.update_document(0.5)
	for row in list_oracle.rows:
		list.set_element_style("#a", "animation-name", row.names)
		list.update_document(0)
		checks += 1
		if not is_equal_approx(list.query_bounds("#a").size.x, float(row.width)):
			failures += 1
			printerr("FAIL animation list: ", row.names, " expected ", row.width)
	list.free()
	var timing := WevaDocument.new()
	timing.paused = true
	timing.document_size = Vector2(400, 300)
	timing.html = '<div id="a">HUD</div>'
	timing.css = '#a{width:20px;animation:pulse 2s linear infinite paused}@keyframes pulse{from{width:100px}to{width:200px}}'
	add_child(timing)
	for step in [["paused", 0.5, 100.0], ["running", 0.5, 125.0], ["paused", 1.0, 125.0], ["running", 0.5, 150.0]]:
		timing.set_element_style("#a", "animation-play-state", step[0])
		timing.update_document(step[1])
		checks += 1
		if not is_equal_approx(timing.query_bounds("#a").size.x, float(step[2])):
			failures += 1
			printerr("FAIL animation pause/resume: ", step)
	timing.free()
	var delayed := WevaDocument.new()
	delayed.paused = true
	delayed.document_size = Vector2(400, 300)
	delayed.html = '<div id="a">HUD</div>'
	delayed.css = '#a{width:20px;animation:pulse 2s linear 0.5s infinite}@keyframes pulse{from{width:100px}to{width:200px}}'
	add_child(delayed)
	for step in [[0.0, 20.0], [0.25, 20.0], [0.5, 112.5]]:
		delayed.update_document(step[0])
		checks += 1
		if not is_equal_approx(delayed.query_bounds("#a").size.x, float(step[1])):
			failures += 1
			printerr("FAIL animation delay: ", step)
	delayed.free()
	# Exercise Godot's normal process loop, without explicit update_document calls.
	var automatic := WevaDocument.new()
	automatic.document_size = Vector2(400, 300)
	automatic.html = '<div id="a">HUD</div>'
	automatic.css = '#a{width:20px;animation:pulse 0.05s linear 0.05s forwards paused}@keyframes pulse{from{width:100px}to{width:200px}}'
	add_child(automatic)
	await get_tree().create_timer(0.2).timeout
	checks += 1
	if not is_equal_approx(automatic.query_bounds("#a").size.x, 20.0):
		failures += 1
		printerr("FAIL automatically processed paused delay")
	automatic.set_element_style("#a", "animation-play-state", "running")
	await get_tree().create_timer(0.3).timeout
	checks += 1
	if not is_equal_approx(automatic.query_bounds("#a").size.x, 200.0):
		failures += 1
		printerr("FAIL automatically processed delayed animation completion")
	automatic.free()
	print("Conditional keyframes: ", checks, " checks, ", failures, " failures")
	get_tree().quit(1 if failures else 0)
