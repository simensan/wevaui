extends Node

var checks := 0
var failures := 0

func require_width(doc: WevaDocument, expected: float) -> void:
	checks += 1
	if not is_equal_approx(doc.query_bounds("#a").size.x, expected):
		failures += 1
		printerr("FAIL effect list: expected width ", expected, " got ", doc.query_bounds("#a").size.x)

func make_doc(css: String) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.paused = true
	doc.document_size = Vector2(800, 600)
	doc.html = '<div id="a">Panel</div>'
	doc.css = css
	add_child(doc)
	doc.update_document(0)
	return doc

func _ready() -> void:
	for count: int in [32, 33, 64]:
		for earlier: bool in [false, true]:
			var names := "width" if earlier else "opacity"
			for i in range(1, count):
				names += ",opacity"
			names += ",width"
			var doc := make_doc('#a{width:100px;height:20px;transition-property:' + names + ';transition-duration:1s,2s;transition-timing-function:linear}')
			doc.set_element_style("#a", "width", "300px")
			doc.update_document(0)
			doc.update_document(0.5)
			require_width(doc, 150 if count % 2 else 200)
			doc.free()
	for count: int in [8, 16]:
		var names := "none,".repeat(count) + "pulse"
		var doc := make_doc('#a{width:20px;animation-name:' + names + ';animation-duration:2s;animation-timing-function:linear;animation-fill-mode:forwards}@keyframes pulse{from{width:100px}to{width:300px}}')
		doc.update_document(0.5)
		require_width(doc, 150)
		doc.set_element_style("#a", "animation-play-state", "paused")
		doc.update_document(0.5)
		require_width(doc, 150)
		doc.set_element_style("#a", "animation-name", "pulse")
		doc.update_document(0)
		require_width(doc, 150)
		doc.set_element_style("#a", "animation-play-state", "running")
		doc.update_document(0.5)
		require_width(doc, 200)
		doc.set_element_style("#a", "animation-name", "none")
		doc.update_document(0)
		require_width(doc, 20)
		doc.free()
	print("Long effect lists: ", checks, " checks, ", failures, " failures")
	get_tree().quit(1 if failures else 0)
