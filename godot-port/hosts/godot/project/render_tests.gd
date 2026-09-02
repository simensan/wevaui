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
	_test_backdrop_filter_crosses_the_boundary()
	_test_content_size()
	_test_documents_do_not_disturb_each_other()
	_test_script_bindings()
	_test_click_signals()
	_test_focus_navigation()
	_test_form_binding()
	_test_demo_scene_pattern()
	_test_scrolling()

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

func _test_backdrop_filter_crosses_the_boundary() -> void:
	# backdrop-filter travels as its own draw kind, because it is the one
	# effect the core cannot decompose into triangles -- it reads the
	# destination. All this can check headless is that the extra draw crosses
	# the ABI, since --headless does not run _draw at all; that the host then
	# paints it correctly is what hosts/godot/compare_render.py measures.
	var plain := _make_doc(
		"<body><div id='a'></div></body>",
		"#a { display: block; width: 100px; height: 50px; background-color: #ff0000 }")
	var filtered := _make_doc(
		"<body><div id='a'></div></body>",
		"#a { display: block; width: 100px; height: 50px; background-color: #ff0000;" +
		" backdrop-filter: blur(8px) saturate(1.5) }")
	_check(filtered.get_draw_count() == plain.get_draw_count() + 1,
		"backdrop-filter adds exactly one draw")

	# And a list that resolves to no change costs nothing, so a host is never
	# asked to read back its framebuffer in order to multiply by one.
	var identity := _make_doc(
		"<body><div id='a'></div></body>",
		"#a { display: block; width: 100px; height: 50px; background-color: #ff0000;" +
		" backdrop-filter: saturate(1) }")
	_check(identity.get_draw_count() == plain.get_draw_count(),
		"an identity backdrop-filter adds no draw")
	plain.queue_free()
	filtered.queue_free()
	identity.queue_free()


func _test_content_size() -> void:
	# What a host needs in order to scroll a document: how far it reaches, which
	# is not the viewport. Half the sample corpus lays out past the box it is
	# given, and painting stops at that box -- so a host that wants the rest has
	# to lay the page out again at this height.
	var short_doc := _make_doc(
		"<body><div id='a'></div></body>",
		"html, body { margin: 0 } #a { display: block; height: 30px }",
		Vector2(200, 100))
	# The viewport is the floor: a page that fits reports the box it fits in, so
	# "is there anything to scroll to" answers no.
	_check(short_doc.get_content_size() == Vector2(200, 100),
		"a page inside its viewport reports the viewport")

	var tall_doc := _make_doc(
		"<body><div></div><div></div><div></div></body>",
		"html, body { margin: 0 } div { display: block; height: 80px }",
		Vector2(200, 100))
	var reach := tall_doc.get_content_size()
	_check(_approx(reach.y, 240.0), "three 80px blocks reach 240px, not the 100px viewport")
	_check(_approx(reach.x, 200.0), "width did not overflow, so it stays the viewport's")
	short_doc.queue_free()
	tall_doc.queue_free()


func _test_documents_do_not_disturb_each_other() -> void:
	# Creating and destroying documents must leave the survivors alone.
	#
	# It did not. Each document built its own SystemFont faces for the symbols
	# and emoji the theme font lacks, and a SystemFont's TextServer RIDs are not
	# its alone -- so releasing one document's faces invalidated fonts a LATER
	# document was still drawing with. Clicking through the sample gallery hit
	# it within four documents: 59,664 "font is null" errors and pages that came
	# out with no text at all.
	const HTML := "<body><p id=t>Text with a star</p></body>"
	const CSS := "#t { font-size: 16px; color: #fff }"

	var first := _make_doc(HTML, CSS)
	var want := first.get_triangle_count()
	_check(want > 0, "the reference document draws something")

	# Enough churn to have broken it: build and destroy several documents while
	# the first is still alive.
	for i in 6:
		var scratch := _make_doc(HTML, CSS)
		_check(scratch.get_triangle_count() == want,
			"a later document draws the same as the first")
		remove_child(scratch)
		scratch.free()

	# And the survivor is untouched, which is the half that actually broke: the
	# glyphs it had already rasterized came from fonts another document freed.
	first.update_document()
	_check(first.get_triangle_count() == want,
		"the surviving document still draws the same after others were freed")
	remove_child(first)
	first.free()


# What a GDScript author actually does with this: change what the document
# says, toggle a class to restyle it, and hear about a click. None of it was
# reachable from a script before.
func _test_script_bindings() -> void:
	var doc := _make_doc(
		"<body><div id='label'>before</div><div id='row'>name<span id='icon'>*</span></div></body>",
		"#label { width: 100px; height: 20px } .hot { background-color: #ff0000 }")

	_check(doc.has_element("#label"), "has_element finds one that is there")
	_check(not doc.has_element("#nothing"), "has_element rejects one that is not")
	_check(doc.get_element_text("#label") == "before", "text reads back")

	_check(doc.set_element_text("#label", "after"), "setting text succeeds")
	doc.update_document()
	_check(doc.get_element_text("#label") == "after", "text round-trips through the binding")

	# Child elements survive a text change, so a row keeps its icon.
	_check(doc.set_element_text("#row", "renamed"), "a row's text can be set")
	doc.update_document()
	_check(doc.has_element("#icon"), "setting a row's text keeps the element inside it")

	# A class toggle is the ordinary way to drive a restyle from game logic.
	var before := doc.get_draw_count()
	_check(doc.add_element_class("#label", "hot"), "a class can be added")
	doc.update_document()
	_check(doc.get_draw_count() > before, "the added class paints something new")
	_check(doc.remove_element_class("#label", "hot"), "a class can be removed")
	doc.update_document()
	_check(doc.get_draw_count() == before, "removing it puts the document back")

	# Toggling to the state it is already in is not an error.
	_check(doc.toggle_element_class("#label", "hot", false), "toggling off an absent class is fine")
	doc.queue_free()


# A click reaches a script as a signal naming the element.
func _test_click_signals() -> void:
	var doc := _make_doc(
		"<body><div id='btn'></div></body>",
		"html, body { margin: 0 } #btn { width: 100px; height: 40px; background-color: #0f0 }")

	var clicked: Array = []
	var entered: Array = []
	doc.element_clicked.connect(func(id): clicked.append(id))
	doc.element_entered.connect(func(id): entered.append(id))

	# Driving the document directly, since a headless run has no real pointer.
	_check(doc.element_id_at(Vector2(50, 20)) == "btn", "hit testing finds the button")

	doc.set_pointer(Vector2(50, 20), 0)
	doc.set_pointer(Vector2(50, 20), 1)
	doc.set_pointer(Vector2(50, 20), 0)
	doc.update_document()

	_check(entered.has("btn"), "entering the button raises element_entered")
	_check(clicked.has("btn"), "pressing and releasing on it raises element_clicked")

	# Pressing on it and releasing elsewhere is a drag, not a click.
	clicked.clear()
	doc.set_pointer(Vector2(50, 20), 1)
	doc.set_pointer(Vector2(50, 300), 1)
	doc.set_pointer(Vector2(50, 300), 0)
	doc.update_document()
	_check(not clicked.has("btn"), "releasing away from the button is not a click")
	doc.queue_free()


# Tab order, from a script. Focus is the one thing only the document can work
# out, so it is the one key the engine acts on itself.
func _test_focus_navigation() -> void:
	var doc := _make_doc(
		"<body><button id='one'>1</button><div id='plain'>x</div>" +
		"<button id='two'>2</button><button id='three' disabled>3</button></body>",
		"button, div { display: block }")

	var focused: Array = []
	doc.element_focused.connect(func(id): focused.append(id))

	_check(doc.focus_next(false) == "one", "tab lands on the first focusable")
	# The plain div is not focusable and the disabled button is out of the ring.
	_check(doc.focus_next(false) == "two", "tab skips what cannot take focus")
	_check(doc.focus_next(false) == "one", "tab wraps round")
	_check(doc.focus_next(true) == "two", "shift-tab goes the other way")
	_check(focused.has("one") and focused.has("two"), "focus changes reach a script")

	# Focus by selector still works, and drives the same signal.
	_check(doc.set_focus("#one"), "focus can be set by selector")
	doc.queue_free()


# Two-way binding to a form control: a script sets it, a user changes it, and
# the script hears about it. These painted correctly from their attributes
# before and behaved like pictures.
func _test_form_binding() -> void:
	var doc := _make_doc(
		"<body><input id='box' type='checkbox'>" +
		"<input id='field' type='text' value='start'></body>",
		"html, body { margin: 0 } input { display: block; width: 60px; height: 20px }")

	var changes: Array = []
	doc.value_changed.connect(func(id, value): changes.append([id, value]))

	_check(doc.get_element_value("#field") == "start", "a field's value reads back")
	_check(doc.get_element_value("#box") == "", "an unchecked box reads empty")

	_check(doc.set_element_value("#field", "bound"), "a value can be set")
	doc.update_document()
	_check(doc.get_element_value("#field") == "bound", "the value round-trips")

	# Clicking the box toggles it, and the script is told.
	var box := doc.query_bounds("#box")
	doc.set_pointer(Vector2(box.position.x + 5, box.position.y + 5), 0)
	doc.set_pointer(Vector2(box.position.x + 5, box.position.y + 5), 1)
	doc.set_pointer(Vector2(box.position.x + 5, box.position.y + 5), 0)
	doc.update_document()
	_check(doc.get_element_value("#box") == "on", "clicking the box checks it")

	var told := false
	for change in changes:
		if change[0] == "box" and change[1] == "on":
			told = true
	_check(told, "the change reaches the script as a signal")
	doc.queue_free()


# The demo scene's own markup, driven the way the demo drives it.
#
# demo.tscn is the worked example a reader copies from, so it is worth knowing
# that the pattern in it actually works rather than that it merely parses.
func _test_demo_scene_pattern() -> void:
	var demo := preload("res://demo.gd")
	var doc := _make_doc(demo.HTML, demo.CSS, Vector2(640, 400))

	_check(doc.has_element("#hp-fill"), "the demo's bar is there")
	_check(doc.get_element_text("#hp-text") == "100 / 100", "the demo's readout starts full")

	var clicked: Array = []
	doc.element_clicked.connect(func(id): clicked.append(id))

	# What _refresh() does, at 40 hp: the readout, an inline width, and the
	# state classes the stylesheet animates off.
	doc.set_element_text("#hp-text", "40 / 100")
	doc.set_element_attribute("#hp-fill", "style", "width: 40%")
	doc.toggle_element_class("#hp-fill", "hurt", true)
	doc.update_document()
	_check(doc.get_element_text("#hp-text") == "40 / 100", "the readout follows the script")

	var bar := doc.query_bounds("#hp-fill")
	var track := doc.query_bounds(".bar")
	# The demo's stylesheet puts a 260ms transition on the bar's width, so it
	# does NOT jump -- which is the point of driving a UI this way, and means
	# the test has to let time pass before measuring.
	_check(bar.size.x == track.size.x, "the bar has not moved yet: it is transitioning")
	doc.update_document(0.3)
	bar = doc.query_bounds("#hp-fill")
	_check(bar.size.x < track.size.x * 0.6, "and lands narrow once the transition runs")

	# Clicking a button reaches the script by id, which is the whole binding.
	var hit := doc.query_bounds("#hit")
	var at := hit.position + hit.size * 0.5
	doc.set_pointer(at, 0)
	doc.set_pointer(at, 1)
	doc.set_pointer(at, 0)
	doc.update_document()
	_check(doc.element_id_at(at) == "hit", "hit testing finds the button through the padding")
	_check(clicked.has("hit"), "clicking the demo's button raises its id")

	# And the form controls the demo exposes.
	_check(doc.get_element_value("#name") == "Vintner", "the demo's field has its value")
	_check(doc.set_element_value("#shield", "on"), "the demo's checkbox can be set")
	_check(doc.get_element_value("#shield") == "on", "and reads back")
	doc.queue_free()


func _test_scrolling() -> void:
	# A list too long for its box: 5 rows of 40 in 100 of room.
	var doc := _make_doc(
		"<body><div id='list'><div id='r0' class='row'></div><div id='r1' class='row'></div>" +
		"<div id='r2' class='row'></div><div id='r3' class='row'></div>" +
		"<div id='r4' class='row'></div></div></body>",
		"html, body { margin: 0 } " +
		"#list { width: 200px; height: 100px; overflow: auto } " +
		".row { height: 40px; background: #445566 }")
	_check(doc.get_element_scroll_max("#list") == Vector2(0, 100),
		"a list taller than its box reports how far it can go")
	_check(doc.get_element_scroll("#list") == Vector2(), "and starts at the top")

	# What a wheel over it does. The point is in document coordinates, which is
	# what _input converts a mouse position into.
	_check(doc.scroll_at(Vector2(100, 50), Vector2(0, 60)), "a wheel over the list scrolls it")
	doc.update_document()
	_check(doc.get_element_scroll("#list") == Vector2(0, 60), "by as much as it was given")
	# Past the end stops at the end, and then says it has nothing left.
	doc.scroll_at(Vector2(100, 50), Vector2(0, 500))
	doc.update_document()
	_check(doc.get_element_scroll("#list") == Vector2(0, 100), "and stops at the bottom")
	_check(not doc.scroll_at(Vector2(100, 50), Vector2(0, 10)),
		"a wheel with nowhere to go is not consumed, so the game behind gets it")
	# Nothing scrollable under the point is the same answer.
	_check(not doc.scroll_at(Vector2(380, 190), Vector2(0, 40)),
		"nor is one over something that does not scroll")

	# By name, which is what a script that owns a panel does.
	doc.set_element_scroll("#list", Vector2(0, 20))
	doc.update_document()
	_check(doc.get_element_scroll("#list") == Vector2(0, 20), "a script can set the position")
	_check(doc.scroll_element("#list", Vector2(0, 25)), "and nudge it")
	doc.update_document()
	_check(doc.get_element_scroll("#list") == Vector2(0, 45), "relative to where it was")
	_check(not doc.scroll_element("#nope", Vector2(0, 10)),
		"an unmatched selector scrolls nothing rather than erroring")

	# Hit testing follows the scroll: 45 down, row 1 (which lies from 40 to
	# 80) is what covers the top of the list, and it is what a click there
	# has to reach -- you click what you can SEE.
	_check(doc.element_id_at(Vector2(100, 5)) == "r1",
		"a click lands on the row that scrolled under the pointer")
	_check(doc.element_id_at(Vector2(100, 95)) == "r3",
		"and on the right one at the other end")
	# Bringing something into view, which is what a chat pane does with a
	# new message and a list does when the selection moves past its edge.
	doc.set_element_scroll("#list", Vector2())
	doc.update_document()
	_check(doc.scroll_into_view("#r4"), "the last row can be brought into view")
	doc.update_document()
	_check(doc.get_element_scroll("#list") == Vector2(0, 100),
		"by the least that shows it: it sits against the bottom edge")
	_check(not doc.scroll_into_view("#nope"), "and an unmatched selector moves nothing")
	doc.queue_free()
