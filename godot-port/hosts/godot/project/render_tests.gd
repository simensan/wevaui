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
	_test_building_from_data()
	_test_keyboard_scrolling()
	_test_selection()
	_test_select_dropdown()
	_test_data_binding()
	_test_event_handlers()
	_test_commit_submit_and_scroll_signals()
	_test_word_editing_and_undo()
	_test_cjk_text()

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
	# The demo drives its document entirely through bindings: the script holds
	# state, sets `data`, and names methods in the markup. This walks the same
	# path, because a worked example that has quietly stopped working is worse
	# than none.
	var demo := preload("res://demo.gd")
	var doc := _make_doc(demo.HTML, demo.CSS, Vector2(640, 480))

	doc.data = {
		"Hp": 100, "Hurt": false, "Low": false, "Alive": true,
		"AtFullHealth": true, "Status": "ready", "Log": [],
	}
	doc.update_document()
	_check(doc.query_text(".value") == "100 / 100", "the readout reads the data")
	_check(doc.get_element_attribute(".fill", "style") == "width: 100%",
		"and the bar's width comes from the same number")
	_check(doc.query_text(".status") == "ready", "as does the status line")

	# At full health the heal button is off, and the markup says so -- the
	# script only reports the state.
	_check(doc.get_element_attribute("button:nth-of-type(2)", "class").contains("off"),
		"a button disables itself from the data")

	# Hurt: the width follows, the class goes on, and the transition means the
	# bar does NOT jump -- which is the point of leaving it to CSS.
	doc.data = {
		"Hp": 40, "Hurt": true, "Low": false, "Alive": true,
		"AtFullHealth": false, "Status": "40 hp", "Log": [],
	}
	doc.update_document()
	_check(doc.query_text(".value") == "40 / 100", "the readout follows")
	_check(doc.get_element_attribute(".fill", "class").contains("hurt"),
		"and the state class goes on")
	var track := doc.query_bounds(".bar")
	_check(doc.query_bounds(".fill").size.x == track.size.x,
		"the bar has not moved yet: it is transitioning")
	doc.update_document(0.3)
	_check(doc.query_bounds(".fill").size.x < track.size.x * 0.6,
		"and lands narrow once the transition runs")

	# The log is a data-each list, so the script appends to an Array rather than
	# building markup.
	doc.data = {
		"Hp": 40, "Hurt": true, "Low": false, "Alive": true, "AtFullHealth": false,
		"Status": "hit", "Log": [
			{"Id": 1, "Text": "took 10 damage", "Good": false, "Bad": true},
			{"Id": 2, "Text": "healed 10", "Good": true, "Bad": false},
		],
	}
	doc.update_document()
	_check(doc.count_elements("#log > .entry") == 2, "one row per log entry")
	_check(doc.query_text("#log > .entry:nth-of-type(1)").strip_edges() == "took 10 damage",
		"filled from the item")
	_check(doc.get_element_attribute("#log > .entry:nth-of-type(1)", "class").contains("hurt"),
		"and coloured by its own flags")
	_check(doc.get_element_attribute("#log > .entry:nth-of-type(2)", "class").contains("good"),
		"each row reading its own")

	# A click calls the method the markup named, on the controller.
	var called: Array = []
	doc.handler_invoked.connect(func(handler, _id): called.append(handler))
	var button := doc.query_bounds("button:nth-of-type(1)")
	var at := button.position + button.size * 0.5
	doc.set_pointer(at, 0)
	doc.set_pointer(at, 1)
	doc.set_pointer(at, 0)
	doc.update_document()
	_check(called.has("take_damage"), "the button calls what the markup named it")

	# And the form controls the demo exposes still read back.
	_check(doc.get_element_value("#name") == "Vintner of Halden", "the field has its value")
	_check(doc.get_element_value("#quality") == "med", "and the select its choice")
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


func _test_building_from_data() -> void:
	# The binding a game actually needs: a list whose length is the game state,
	# not the markup. None of this can be written in advance.
	var doc := _make_doc(
		"<body><div id='log'></div></body>",
		"html, body { margin: 0 } #log { width: 200px } .line { height: 20px }")
	_check(doc.count_elements(".line") == 0, "an empty list starts empty")

	for message in ["found a key", "the door opens", "something moves"]:
		_check(doc.append_html("#log", "<div class='line'>%s</div>" % message),
			"a row can be appended")
	doc.update_document()
	_check(doc.count_elements(".line") == 3, "one row per message")
	_check(doc.query_bounds("#log").size.y == 60, "and the list is as tall as its rows")

	# Rows are addressed the way CSS addresses them, so a script that can style
	# a list can fill it without a second naming scheme.
	_check(doc.query_text("#log .line:nth-child(2)") == "the door opens",
		"a row can be read back by position")
	doc.set_element_text("#log .line:nth-child(2)", "the door slams")
	doc.update_document()
	_check(doc.query_text("#log .line:nth-child(2)") == "the door slams",
		"and written the same way")

	# Trimming the log from the top, which is what a bounded log does.
	_check(doc.remove_element("#log .line:nth-child(1)"), "the oldest row can go")
	doc.update_document()
	_check(doc.count_elements(".line") == 2, "leaving the rest")
	_check(doc.query_text("#log .line:nth-child(1)") == "the door slams",
		"and the next one moves up")

	# And a wholesale redraw of the panel.
	_check(doc.set_element_html("#log", "<div class='line'>a</div><div class='line'>b</div>"),
		"the whole list can be replaced")
	doc.update_document()
	_check(doc.count_elements(".line") == 2, "with what was given")
	_check(doc.query_text("#log .line:nth-child(1)") == "a", "in order")

	# Misses report a miss rather than doing something surprising.
	_check(not doc.append_html("#nope", "<div></div>"), "appending to nothing fails")
	_check(not doc.remove_element("#nope"), "removing nothing fails")
	_check(doc.count_elements("#nope") == 0, "and counting nothing is zero")
	doc.queue_free()


func _test_keyboard_scrolling() -> void:
	# A host with its own input map hands keys over one at a time: a controller
	# whose d-pad should move a list, a menu that decides who gets the keyboard.
	var doc := _make_doc(
		"<body><div id='list'><div class='row'></div><div class='row'></div>" +
		"<div class='row'></div><div class='row'></div><div class='row'></div></div></body>",
		"html, body { margin: 0 } " +
		"#list { width: 200px; height: 100px; overflow: auto } .row { height: 40px }")
	doc.set_focus("#list")
	_check(doc.send_key(KEY_DOWN), "the document takes the key")
	doc.update_document()
	_check(doc.get_element_scroll("#list") == Vector2(0, 40), "and scrolls a line")

	_check(doc.send_key(KEY_PAGEDOWN), "page down is taken too")
	doc.update_document()
	_check(doc.get_element_scroll("#list") == Vector2(0, 100), "to the end of the list")
	doc.send_key(KEY_HOME)
	doc.update_document()
	_check(doc.get_element_scroll("#list") == Vector2(), "and home is the top")

	# A key with nothing to scroll is NOT consumed, so the game behind still
	# gets its own arrows.
	var plain := _make_doc("<body><div id='b'></div></body>",
		"html, body { margin: 0 } #b { width: 50px; height: 20px }")
	plain.set_focus("#b")
	_check(not plain.send_key(KEY_DOWN), "a key with nowhere to scroll is left alone")
	plain.queue_free()

	# Typing, for a host that owns the keyboard.
	var form := _make_doc("<body><input id='f' type='text' value='ab'></body>",
		"html, body { margin: 0 } input { display: block; width: 120px }")
	form.set_focus("#f")
	form.send_text("c")
	_check(form.get_element_value("#f") == "abc", "text can be handed over directly")
	_check(form.send_key(KEY_BACKSPACE), "and so can an editing key")
	_check(form.get_element_value("#f") == "ab", "which edits at the caret")
	form.queue_free()
	doc.queue_free()


func _test_selection() -> void:
	# Shift with the movement keys selects; the document does that itself. What
	# a host has to drive is select-all -- the ABI key enum has no letters, so
	# it never sees Ctrl+A -- and the clipboard, which is the platform's.
	var doc := _make_doc("<body><input id='f' type='text' value='hello world'></body>",
		"html, body { margin: 0 } input { display: block; width: 200px }")
	doc.set_focus("#f")
	_check(doc.get_selected_text() == "", "a fresh field has nothing selected")

	_check(doc.send_key(KEY_LEFT, true, true), "shift and a movement key select")
	_check(doc.send_key(KEY_LEFT, true, true), "and keep going")
	_check(doc.get_selected_text() == "ld", "back over the last two characters")

	# The range says which end it started from, so a host knows the direction.
	var range := doc.get_element_selection("#f")
	_check(range.x == 11 and range.y == 9, "the range runs from the anchor to the cursor")

	_check(doc.select_all(), "select-all is the host's to trigger")
	_check(doc.get_selected_text() == "hello world", "and takes the lot")

	# Typing replaces what is selected, which is the commonest edit there is.
	doc.send_text("x")
	_check(doc.get_element_value("#f") == "x", "typing over a selection replaces it")
	_check(doc.get_selected_text() == "", "and leaves a plain cursor")

	# A script can set one, for its own selection UI.
	doc.set_element_value("#f", "abcdef")
	_check(doc.set_element_selection("#f", 1, 4), "a script can select a range")
	_check(doc.get_selected_text() == "bcd", "and read it back")
	_check(doc.send_key(KEY_BACKSPACE), "backspace over a selection takes the selection")
	_check(doc.get_element_value("#f") == "aef", "not one character")
	# Clicking into a field puts the cursor where the click was, and a double
	# click takes the word -- the platform knows what a double click IS, so the
	# host is what says one happened.
	doc.set_element_value("#f", "hello brave world")
	var box := doc.query_bounds("#f")
	doc.set_pointer(box.position + Vector2(4, box.size.y * 0.5), 1)
	doc.set_pointer(box.position + Vector2(4, box.size.y * 0.5), 0)
	doc.update_document()
	_check(doc.get_element_selection("#f").y == 0,
		"a click at the left edge puts the cursor before the first character")
	_check(doc.select_word_at(box.position + Vector2(10, box.size.y * 0.5)),
		"a double click reaches the document")
	_check(doc.get_selected_text() == "hello", "and takes the word under it")
	doc.queue_free()


func _test_select_dropdown() -> void:
	# A settings screen offering a choice: clicking the select opens its list,
	# clicking a row chooses it, and the choice lands in the DOM -- so the
	# stylesheet, the paint and the script all read the same thing.
	var doc := _make_doc(
		"<body><select id='q'><option value='low'>Low</option>" +
		"<option value='med' selected>Medium</option>" +
		"<option value='high'>High</option></select></body>",
		"html, body { margin: 0 } select { display: block; width: 160px; height: 28px }")
	_check(doc.get_element_value("#q") == "med", "the value is the chosen option's")

	var box := doc.query_bounds("#q")
	# Focused but closed, which is what it goes back to once a row is taken:
	# an unfocused frame has no focus ring in it, so the ring would be
	# counted as the list and the list would look like it never closed.
	doc.set_focus("#q")
	doc.update_document()
	var closed := doc.get_draw_count()
	doc.set_pointer(box.position + box.size * 0.5, 1)
	doc.update_document()
	_check(doc.get_open_select() == "q", "pressing it opens the list")
	_check(doc.get_draw_count() > closed, "and the list is drawn over the page")

	# The rows sit under the control, in order.
	doc.set_pointer(box.position + Vector2(box.size.x * 0.5, box.size.y + 4), 0)
	doc.set_pointer(box.position + Vector2(box.size.x * 0.5, box.size.y + 4), 1)
	doc.update_document()
	_check(doc.get_element_value("#q") == "low", "clicking the first row chooses it")
	_check(doc.get_open_select() == "", "and closes the list")
	_check(doc.get_draw_count() == closed, "which takes the list out of the frame")

	# A host can drive it, for a controller or its own menu routing.
	_check(doc.open_select("#q"), "a script can open it")
	_check(doc.get_open_select() == "q", "and see that it is open")
	doc.send_key(KEY_DOWN)
	doc.send_key(KEY_ENTER)
	_check(doc.get_element_value("#q") == "med", "the keyboard walks the list and takes one")
	doc.close_select()
	_check(doc.get_open_select() == "", "and a script can close it")
	doc.queue_free()


func _test_data_binding() -> void:
	# `{{ path }}` in the markup and a Dictionary in the script. The script says
	# WHAT changed; the markup says where it is shown -- which is the point of
	# writing a UI in HTML rather than in setter calls.
	var doc := _make_doc(
		"<body><p id='label'>{{ Player.Name }} — {{ Player.Gold }}g</p>" +
		"<div id='bar' class='bar' style='width: {{ Player.Hp }}%'" +
		" data-class-hurt='Player.Hurt'></div></body>",
		"html, body { margin: 0 } .bar { height: 10px }")
	doc.data = {
		"Player": {"Name": "Vintner", "Gold": 120, "Hp": 40, "Hurt": true}
	}
	doc.update_document()
	_check(doc.query_text("#label") == "Vintner — 120g", "text follows the data")
	_check(doc.get_element_attribute("#bar", "style") == "width: 40%",
		"and so does an attribute")
	_check(doc.get_element_attribute("#bar", "class") == "bar hurt",
		"data-class- adds its one class and leaves the rest")

	# The markup stays the template: setting the data again refills it.
	doc.data = {
		"Player": {"Name": "Halden", "Gold": 5, "Hp": 90, "Hurt": false}
	}
	doc.update_document()
	_check(doc.query_text("#label") == "Halden — 5g", "and again when the data moves")
	_check(doc.get_element_attribute("#bar", "style") == "width: 90%", "the attribute refills")
	_check(doc.get_element_attribute("#bar", "class") == "bar", "and the class comes off")

	# A Callable takes over when the state lives somewhere a Dictionary cannot
	# reach -- a singleton, a resource, a computed value.
	var doc2 := _make_doc("<body><p id='t'>{{ Score }} / {{ Best }}</p></body>",
		"html, body { margin: 0 }")
	var scores := {"Score": 40, "Best": 99}
	doc2.set_data_source(func(path): return scores.get(path))
	doc2.update_document()
	_check(doc2.query_text("#t") == "40 / 99", "a Callable resolves the paths")
	scores["Score"] = 41
	_check(doc2.refresh_bindings() > 0, "and a refresh picks up what it now returns")
	doc2.update_document()
	_check(doc2.query_text("#t") == "41 / 99", "which lands in the document")

	# `data-each` binds a LIST: one row per item, from an Array in the data.
	# Until this an inventory or a quest log had to be built by hand with
	# append_html, the script deciding what shape the markup should take.
	var list := _make_doc(
		"<body><ul id='quests'>" +
		"<template data-each='Quests as quest' data-key='Id'>" +
		"<li class='row'>{{ $index }}. {{ quest.Title }}</li>" +
		"</template></ul></body>",
		"html, body { margin: 0 } li { height: 12px }")
	list.data = {"Quests": [
		{"Id": "a", "Title": "Find the key"},
		{"Id": "b", "Title": "Open the door"},
	]}
	list.update_document()
	_check(list.count_elements("#quests > .row") == 2, "one row per item")
	_check(list.query_text("#quests > .row:nth-of-type(1)") == "0. Find the key",
		"filled from the item, with its index")
	_check(list.query_text("#quests > .row:nth-of-type(2)") == "1. Open the door",
		"and so is the second")
	_check(list.query_bounds("#quests").size.y == 24, "and they are laid out")

	# The list growing adds a row; shrinking takes one away.
	list.data = {"Quests": [
		{"Id": "a", "Title": "Find the key"},
		{"Id": "b", "Title": "Open the door"},
		{"Id": "c", "Title": "Leave"},
	]}
	list.update_document()
	_check(list.count_elements("#quests > .row") == 3, "a new item makes a new row")
	list.data = {"Quests": []}
	list.update_document()
	_check(list.count_elements("#quests > .row") == 0, "and an empty list is empty")
	list.queue_free()
	doc2.queue_free()
	doc.queue_free()


func _test_event_handlers() -> void:
	# `on-click="OnStart"` names the method; the script supplies the object. The
	# markup can then be rearranged, renamed or wrapped without the script
	# hearing about it -- which is why it beats matching on element ids.
	var doc := _make_doc(
		"<body><div id='panel' on-click='OnAnything'>" +
		"<button id='go' on-click='OnStart'>Start</button>" +
		"<button id='plain'>Plain</button></div></body>",
		"html, body { margin: 0 } button { display: block; width: 100px; height: 30px }")

	var seen: Array = []
	doc.handler_invoked.connect(func(handler, id): seen.append([handler, id]))
	var box := doc.query_bounds("#go")
	var at := box.position + box.size * 0.5
	doc.set_pointer(at, 0)
	doc.set_pointer(at, 1)
	doc.set_pointer(at, 0)
	doc.update_document()
	_check(seen.size() > 0 and seen[0][0] == "OnStart",
		"the handler the markup named arrives with the event")
	_check(seen[0][1] == "go", "beside the element it happened on")

	# A button with no handler of its own takes the container's, so a whole
	# panel can be handled in one place.
	seen.clear()
	box = doc.query_bounds("#plain")
	at = box.position + box.size * 0.5
	doc.set_pointer(at, 0)
	doc.set_pointer(at, 1)
	doc.set_pointer(at, 0)
	doc.update_document()
	_check(seen.size() > 0 and seen[0][0] == "OnAnything",
		"a handler on an ancestor catches what happens inside it")
	doc.queue_free()


func _test_commit_submit_and_scroll_signals() -> void:
	# Three things a script needs that a raw `value_changed` cannot say: the
	# user is DONE editing, a form was submitted, and a list has been scrolled.
	var doc := _make_doc(
		"<body><form id='search' on-submit='OnSearch'>" +
		"<input id='q' type='text' value='' on-change='OnCommit'>" +
		"<button id='go'>Go</button></form>" +
		"<div id='list'><div class='row'></div><div class='row'></div>" +
		"<div class='row'></div></div></body>",
		"html, body { margin: 0 } input, button { display: block; width: 100px; height: 24px }" +
		" #list { width: 120px; height: 50px; overflow: auto } .row { height: 40px }")

	var committed: Array = []
	var submitted: Array = []
	var scrolled: Array = []
	doc.value_committed.connect(func(id, value): committed.append([id, value]))
	doc.form_submitted.connect(func(id): submitted.append(id))
	doc.element_scrolled.connect(func(id, x, y): scrolled.append([id, x, y]))

	# Typing raises `value_changed` every keystroke and commits nothing.
	doc.set_focus("#q")
	doc.send_text("ale")
	doc.update_document()
	_check(committed.is_empty(), "typing does not commit")

	# The focus leaving does, once, with what the field ended up holding.
	doc.set_focus("#go")
	doc.update_document()
	_check(committed.size() == 1 and committed[0][0] == "q" and committed[0][1] == "ale",
		"leaving a field commits what it holds")

	# Enter in the field submits the form around it, and the signal carries
	# the FORM's id -- which is what a handler is written against.
	doc.set_focus("#q")
	doc.send_key(KEY_ENTER)
	doc.update_document()
	_check(submitted.size() == 1 and submitted[0] == "search",
		"Enter in a field submits the form it is in")

	# So does the button in it, with no `type` needed: HTML says a button in a
	# form submits unless it says otherwise.
	submitted.clear()
	var box := doc.query_bounds("#go")
	var at := box.position + box.size * 0.5
	doc.set_pointer(at, 1)
	doc.set_pointer(at, 0)
	doc.update_document()
	_check(submitted.size() == 1 and submitted[0] == "search", "a button in a form submits it")

	# And a scrolled list says where it ended up, so a script can page in more
	# rows without asking the document every frame.
	box = doc.query_bounds("#list")
	doc.scroll_at(box.position + box.size * 0.5, Vector2(0, 20))
	doc.update_document()
	_check(scrolled.size() == 1 and scrolled[0][0] == "list" and _approx(scrolled[0][2], 20),
		"a scroll reports the offset it landed on")
	doc.queue_free()


func _test_word_editing_and_undo() -> void:
	# The editing a text field needs before it stops being a toy: Ctrl+Arrow
	# by the word, Ctrl+Backspace to fix one, and Ctrl+Z to take it back.
	var doc := _make_doc(
		"<body><input id='t' type='text' value='alpha beta gamma'></body>",
		"html, body { margin: 0 } input { display: block; width: 300px; height: 30px }")
	doc.set_focus("#t")

	# Ctrl+Backspace eats a word.
	doc.send_key(KEY_BACKSPACE, true, false, true)
	doc.update_document()
	_check(doc.get_element_value("#t") == "alpha beta ", "Ctrl+Backspace deletes a word")

	# And Ctrl+Z brings it back, whole.
	_check(doc.undo(), "there is something to undo")
	doc.update_document()
	_check(doc.get_element_value("#t") == "alpha beta gamma", "undo restores the deleted word")
	_check(doc.redo(), "and redo takes it away again")
	doc.update_document()
	_check(doc.get_element_value("#t") == "alpha beta ", "redo reapplies the edit")

	# A run of typing is one undo step, so Ctrl+Z after a word removes the
	# word rather than its last letter.
	doc.send_text("xyz")
	doc.update_document()
	_check(doc.get_element_value("#t") == "alpha beta xyz", "typing appends")
	_check(doc.undo(), "the typed run undoes")
	doc.update_document()
	_check(doc.get_element_value("#t") == "alpha beta ", "as one step, not three")

	# Ctrl+Left moves by the word, which the selection shows.
	doc.set_focus("#t")
	doc.send_key(KEY_LEFT, true, true, true)   # shift + ctrl
	doc.update_document()
	_check(doc.get_selected_text() == "beta ", "Shift+Ctrl+Left selects a word")
	doc.queue_free()


func _test_cjk_text() -> void:
	# Japanese has no spaces, so a line that can only break at one cannot break
	# at all. The core breaks between characters instead; this checks it
	# through the host, with Godot's own font rather than the core's ASCII stub.
	var doc := _make_doc(
		"<body><div id='a'>日本語のテキストです</div></body>",
		"html, body { margin: 0 } #a { display: block; width: 60px; font-size: 24px;" +
		" line-height: 1.5 }")
	var one_line := 24.0 * 1.5
	var height := doc.query_bounds("#a").size.y
	_check(height > one_line * 1.5, "a CJK paragraph wraps instead of overflowing")

	doc.queue_free()

	# `nowrap` proves the wrap came from the break rule and not from something
	# else deciding the box's height. A second document, because `css` ADDS a
	# stylesheet rather than replacing one -- reusing this one would leave both
	# rules live and test the cascade instead of the break.
	var fixed := _make_doc(
		"<body><div id='a'>日本語のテキストです</div></body>",
		"html, body { margin: 0 } #a { display: block; width: 60px; font-size: 24px;" +
		" line-height: 1.5; white-space: nowrap }")
	_check(fixed.query_bounds("#a").size.y < one_line * 1.5, "nowrap still forbids the break")
	fixed.queue_free()

	# And whether the characters DRAW is the font's question, not the layout's.
	# The core's built-in face is a 5x7 ASCII bitmap with no CJK glyphs, and
	# Godot's default theme font has none either -- so this reports what the
	# host's font covers rather than asserting a coverage it does not have.
	var drawn := _make_doc(
		"<body><div id='a'>日本語</div></body>",
		"html, body { margin: 0 } #a { display: inline-block; font-size: 24px }")
	var latin := _make_doc(
		"<body><div id='a'>abc</div></body>",
		"html, body { margin: 0 } #a { display: inline-block; font-size: 24px }")
	_check(latin.get_triangle_count() >= 6, "Latin draws, so the text path itself works")

	# Godot's theme font has no CJK glyphs, so drawing them depends on the
	# system fallback chain the host builds. Assert it only where the machine
	# actually has one of those faces -- a build box with no Japanese font
	# installed cannot draw Japanese, and failing there would be reporting the
	# machine rather than the code.
	var installed := OS.get_system_fonts()
	var cjk_face := ""
	for name in ["Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic", "Hiragino Sans",
			"Noto Sans CJK JP", "Noto Sans JP", "Microsoft YaHei", "Malgun Gothic",
			"Noto Sans CJK SC", "WenQuanYi Micro Hei", "Droid Sans Fallback"]:
		if installed.has(name):
			cjk_face = name
			break
	if cjk_face.is_empty():
		print("godot host: no CJK font installed, glyph coverage not checked")
	else:
		_check(drawn.get_triangle_count() >= 6,
			"CJK draws through the system fallback (%s)" % cjk_face)
	drawn.queue_free()
	latin.queue_free()
