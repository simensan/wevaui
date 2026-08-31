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
	_test_engine_font_is_adopted()
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

func _test_engine_font_is_adopted() -> void:
	# Falling back to the core's 5x7 stub is silent by design — text still lays
	# out and still renders — so without this check a broken font backend looks
	# like a font choice.
	# inline-block, not block: a block fills its containing block whatever the
	# font, so its width would report the same either way and the measurement
	# check below would pass without measuring anything.
	var doc := _make_doc(
		"<body><div id='a'>Hello</div></body>",
		"#a { display: inline-block; font-size: 16px }")
	_check(doc.has_engine_font(), "the engine's fallback font was adopted")

	# The engine face must actually be what gets measured, not just what gets
	# drawn: a backend wired into paint but not into metrics lays text out to
	# one face and draws it with another.
	var engine_width := doc.query_bounds("#a").size.x
	doc.use_engine_font = false
	doc.update_document()
	_check(not doc.has_engine_font(), "turning the engine font off falls back to the stub")
	_check(doc.query_bounds("#a").size.x != engine_width,
		"the two faces measure the text differently")
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
	# The hiding rule is #a[data-hide], not [data-hide]: an id selector is
	# (1,0,0) and an attribute selector (0,1,0), so a bare [data-hide] loses the
	# cascade to the #a rule and display stays `block`. Getting this wrong is
	# what the first version of this test did, and the engine was right.
	var doc := _make_doc(
		"<body><div id='a'>x</div></body>",
		"#a { display: block; width: 10px; height: 10px } #a[data-hide] { display: none }")
	_check(doc.query_bounds("#a").size.x > 0.0, "the box exists before the attribute is set")

	_check(doc.set_element_attribute("#a", "data-hide", "1"), "setting an attribute succeeds")
	doc.update_document()
	# `display: none` generates no box at all, so the query finds nothing.
	_check(doc.query_bounds("#a") == Rect2(), "the restyle removed the box")

	# And back: a restyle that only ever hid things would pass the check above
	# for the wrong reason.
	# Removal, not set-to-empty: [data-hide] is a presence selector, so an empty
	# value would still match and the box would stay hidden.
	_check(doc.remove_element_attribute("#a", "data-hide"), "removing an attribute succeeds")
	doc.update_document()
	_check(_approx(doc.query_bounds("#a").size.x, 10.0), "removing the attribute restores the box")

	_check(not doc.set_element_attribute("#nope", "x", "y"),
		"setting an attribute on a missing element reports failure")
	_check(not doc.remove_element_attribute("#nope", "x"),
		"removing an attribute from a missing element reports failure")
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
