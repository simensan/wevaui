extends SceneTree

func _initialize() -> void:
	call_deferred("run_bench")

func run_bench() -> void:
	var doc := WevaDocument.new()
	doc.size = Vector2(640, 480)
	doc.paused = true
	doc.css = 'html,body{margin:0}select{display:block;width:260px;height:160px;padding:0;border:0}option{height:24px;padding:0}'
	var markup := '<select id="s" multiple size="6">'
	for i in 1000:
		markup += '<option id="o%d" value="%d">Item %d</option>' % [i,i,i]
	doc.html = markup + '</select>'
	root.add_child(doc)
	doc.update_document(0)
	doc.set_pointer(doc.query_bounds("#o1").get_center(),1)
	doc.set_pointer(doc.query_bounds("#o2").get_center(),1)
	doc.set_pointer(Vector2(130,220),1)
	doc.update_document(0)
	var first := 0
	var total := 0
	var maximum := 0
	for i in 221:
		var start := Time.get_ticks_usec()
		doc.update_document(1.0/60.0)
		var elapsed := Time.get_ticks_usec() - start
		if i == 0:
			first = elapsed
		elif i > 20:
			total += elapsed
			maximum = maxi(maximum,elapsed)
	var offset := doc.get_element_scroll("#s").y
	if offset <= 0 or offset >= doc.get_element_scroll_max("#s").y or doc.get_element_value("#s") != "1,2":
		printerr("FAIL autoscroll benchmark must measure moving, uncommitted selection")
		quit(1)
		return
	print("autoscroll 1000 rows: first %.3f ms, warmed mean %.3f ms, max %.3f ms (200 samples); offset %.1f px" % [first/1000.0,total/200000.0,maximum/1000.0,offset])
	doc.clear_pointer()
	doc.free()
	quit()
