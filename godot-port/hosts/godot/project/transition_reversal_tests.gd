extends Node

var checks := 0
var failures := 0

func require_width(doc: WevaDocument, expected: float) -> void:
	checks += 1
	var actual := doc.query_bounds("#a").size.x
	if absf(actual - expected) >= 0.05:
		failures += 1
		printerr("FAIL transition reversal: expected ", expected, " got ", actual)

func _ready() -> void:
	var cases := [
		["ease", 0.2, [159.046875,145.671875,168.09375,300]],
		["ease", 0.7, [288.140625,281.375,300,300]],
		["ease-in-out", 0.2, [116.328125,105.09375,109.140625,300]],
		["ease-in-out", 0.7, [262.515625,261.3125,281.796875,300]],
		["cubic-bezier(.3,-.8,.7,1.8)", 0.2, [76.484375,100,70.28125,300]],
		["cubic-bezier(.3,-.8,.7,1.8)", 0.7, [293.96875,314.140625,300,300]],
		["steps(4,end)", 0.2, [100,100,100,300]],
		["steps(4,end)", 0.7, [200,200,200,300]],
	]
	for row: Array in cases:
		var doc := WevaDocument.new()
		doc.paused = true
		doc.document_size = Vector2(800, 600)
		doc.html = '<div id="a">Panel</div>'
		doc.css = '#a{width:100px;height:20px;background:red;transition:width 1s %s}' % row[0]
		add_child(doc)
		doc.update_document(0)
		doc.set_element_style("#a", "width", "300px")
		doc.update_document(0)
		doc.update_document(row[1])
		require_width(doc, row[2][0])
		doc.set_element_style("#a", "width", "100px")
		doc.update_document(0)
		doc.update_document(0.05)
		require_width(doc, row[2][1])
		doc.set_element_style("#a", "width", "300px")
		doc.update_document(0)
		doc.update_document(0.1)
		require_width(doc, row[2][2])
		doc.update_document(1)
		require_width(doc, row[2][3])
		doc.free()
	print("Transition reversal: ", checks, " checks, ", failures, " failures")
	get_tree().quit(1 if failures else 0)
