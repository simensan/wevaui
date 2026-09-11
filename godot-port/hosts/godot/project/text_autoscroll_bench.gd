extends SceneTree

func _initialize() -> void:
	call_deferred("run_bench")

func run_bench() -> void:
	for unicode_text in [false,true]:
		var doc := WevaDocument.new()
		doc.size = Vector2(640,480)
		doc.paused = true
		doc.css = 'html,body{margin:0;font:16px sans-serif}input{display:block;width:160px;height:32px;padding:0;border:0;font-size:16px}'
		doc.html = '<input id="f">'
		root.add_child(doc)
		# 4,096 UTF-8 bytes with accents and eight emoji runs. The separate
		# shaping reproduction covers Godot 4.7.2's >32-run engine defect.
		var source := ("á😀b"+"áb".repeat(100)+"abcd").repeat(8) if unicode_text else "a".repeat(4096)
		doc.set_element_value("#f",source)
		doc.set_focus("#f")
		doc.set_element_selection("#f",0,0)
		doc.update_document(0)
		doc.set_pointer(Vector2(20,16),1)
		doc.set_pointer(Vector2(40,16),1)
		doc.set_pointer(Vector2(380,16),1)
		doc.update_document(0)
		var first := 0
		var total := 0
		var maximum := 0
		for i in 221:
			var start := Time.get_ticks_usec()
			doc.update_document(1.0/60.0)
			var elapsed := Time.get_ticks_usec()-start
			if i == 0:
				first = elapsed
			elif i > 20:
				total += elapsed
				maximum = maxi(maximum,elapsed)
		var selected := doc.get_element_selection("#f")
		if selected.y <= selected.x or selected.y >= source.to_utf8_buffer().size():
			printerr("FAIL text autoscroll benchmark must keep exposing new text")
			quit(1)
			return
		print("text autoscroll 4096 bytes %s: first %.3f ms, warmed mean %.3f ms, max %.3f ms (200 samples); selection %s" % ["Unicode" if unicode_text else "ASCII",first/1000.0,total/200000.0,maximum/1000.0,selected])
		doc.clear_pointer()
		doc.free()
	quit()
