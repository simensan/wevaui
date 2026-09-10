extends Node

func _ready() -> void:
	var oracle = JSON.parse_string(FileAccess.get_file_as_string("res://unknown_at_rule_cases.json"))
	var failures := 0
	var checks := 0
	for row in oracle.rows:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.document_size = Vector2(500, 400)
		doc.html = '<from id="a">HUD label</from>'
		doc.css = 'from { color: green }' + row.css
		add_child(doc)
		for wanted: String in ["green", "blue"]:
			doc.update_document(0)
			checks += 1
			if doc.get_computed_style("#a", "color") != wanted:
				failures += 1
				printerr("FAIL at-rule containment: ", row.css, " expected ", wanted)
			doc.css += '@media all { #a { color: blue } }'
		doc.free()
	print("Unknown at-rule: ", checks, " checks, ", failures, " failures")
	get_tree().quit(1 if failures else 0)
