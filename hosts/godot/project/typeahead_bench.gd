extends SceneTree

func _initialize() -> void:
	call_deferred("run_bench")

func run_bench() -> void:
	var doc := WevaDocument.new()
	doc.size = Vector2(640, 480)
	doc.css = 'select{width:260px;height:160px}option{height:24px}'
	var markup := '<select id="s" multiple size="6">'
	for i in 999:
		markup += '<option value="%d">Item %d</option>' % [i, i]
	markup += '<option value="last">Zürich</option></select><button id="other">Other</button>'
	doc.html = markup
	root.add_child(doc)
	doc.update_document(0)
	var total := 0
	var maximum := 0
	var cold := 0
	var input_total := 0
	for i in 221:
		doc.set_focus("#other")
		doc.set_element_value("#s", "0")
		doc.set_focus("#s")
		doc.update_document(0)
		var start := Time.get_ticks_usec()
		doc.send_text("z")
		var input_elapsed := Time.get_ticks_usec() - start
		doc.update_document(0)
		var elapsed := Time.get_ticks_usec() - start
		if doc.get_element_value("#s") != "last":
			printerr("FAIL typeahead benchmark choice")
			quit(1)
			return
		if i == 0:
			cold = elapsed
		elif i > 20:
			total += elapsed
			input_total += input_elapsed
			maximum = maxi(maximum, elapsed)
	print("typeahead 1000 rows: first %.3f ms, warmed mean %.3f ms, max %.3f ms (200 samples)" % [cold / 1000.0, total / 200000.0, maximum / 1000.0])
	print("input/event dispatch %.3f ms; following update %.3f ms" % [input_total / 200000.0, (total - input_total) / 200000.0])
	doc.free()
	quit()
