extends Node2D

# End-to-end checks that the ABI, the extension and the engine agree.
#
# These are deliberately not a re-test of the layout engine — libweva's own
# suite covers that far better. What only Godot can prove is that the binding
# works: that geometry crosses the boundary intact, that element queries return
# what layout computed, and that a restyle round-trips.
#
# Run headless:
#   godot --headless --path project --quit-after 2

var failures := 0
var checks := 0

func _check(condition: bool, description: String) -> void:
	checks += 1
	if not condition:
		failures += 1
		printerr("FAIL  ", description)

func _approx(a: float, b: float, eps := 0.01) -> bool:
	return abs(a - b) < eps

func _make_doc(html: String, css: String, size := Vector2(400, 200)) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.document_size = size
	doc.css = css
	doc.html = html
	add_child(doc)
	doc.update_document()
	return doc

func _ready() -> void:
	_test_geometry_crosses_the_boundary()
	_test_layout_matches_queries()
	_test_text_produces_textured_geometry()
	_test_restyle_round_trips()
	_test_empty_and_malformed_input()

	print("godot host: %d checks, %d failures" % [checks, failures])
	# A non-zero exit code is what makes this usable in CI.
	get_tree().quit(1 if failures > 0 else 0)

func _test_geometry_crosses_the_boundary() -> void:
	var doc := _make_doc(
		"<body><div id='a'></div></body>",
		"#a { display: block; width: 100px; height: 50px; background-color: #ff0000 }")
	_check(doc.get_draw_count() > 0, "a painted box produces at least one draw")
	# Two triangles for the background quad, at minimum.
	_check(doc.get_triangle_count() >= 2, "the background is tessellated into triangles")
	doc.queue_free()

func _test_layout_matches_queries() -> void:
	var doc := _make_doc(
		"<body><div id='a'></div><div id='b' class='x'></div></body>",
		"div { display: block; height: 30px } #b { margin-left: 12px; width: 40px }")
	var a := doc.query_bounds("#a")
	var b := doc.query_bounds(".x")
	_check(_approx(a.position.y, 0.0), "the first block sits at the top")
	_check(_approx(b.position.y, 30.0), "the second block stacks below the first")
	_check(_approx(b.position.x, 12.0), "margin-left offsets the box")
	_check(_approx(b.size.x, 40.0) and _approx(b.size.y, 30.0), "the box takes its declared size")
	# A miss returns an empty rect rather than something a caller might use.
	_check(doc.query_bounds("#nope") == Rect2(), "an unmatched selector returns an empty rect")
	doc.queue_free()

func _test_text_produces_textured_geometry() -> void:
	var doc := _make_doc(
		"<body><div id='a'>Hello</div></body>",
		"#a { display: block; font-size: 16px; color: #00ff00 }")
	# Five glyphs, each a quad, so the run alone is ten triangles.
	_check(doc.get_triangle_count() >= 10, "each glyph contributes a quad")
	_check(doc.query_text("#a") == "Hello", "text crosses the boundary intact")
	doc.queue_free()

func _test_restyle_round_trips() -> void:
	var doc := _make_doc(
		"<body><div id='a'>x</div></body>",
		"#a { display: block; width: 10px; height: 10px } [data-hide] { display: none }")
	_check(doc.query_bounds("#a").size.x > 0.0, "the box exists before the attribute is set")

	_check(doc.set_element_attribute("#a", "data-hide", "1"), "setting an attribute succeeds")
	doc.update_document()
	# `display: none` generates no box at all, so the query finds nothing.
	_check(doc.query_bounds("#a") == Rect2(), "the restyle removed the box")

	_check(not doc.set_element_attribute("#nope", "x", "y"),
		"setting an attribute on a missing element reports failure")
	doc.queue_free()

func _test_empty_and_malformed_input() -> void:
	# A host must be able to construct a document and render nothing without
	# any of this failing — an empty UI is a normal state, not an error.
	var doc := WevaDocument.new()
	add_child(doc)
	doc.update_document()
	_check(doc.get_draw_count() == 0, "an empty document draws nothing")
	_check(doc.query_bounds("#a") == Rect2(), "querying an empty document is safe")
	_check(doc.query_text("#a") == "", "querying text on an empty document is safe")

	doc.html = "<div><span>unclosed"
	doc.css = "#a { color: }  @nonsense {"
	doc.update_document()
	_check(true, "malformed html and css do not crash the host")
	doc.queue_free()
