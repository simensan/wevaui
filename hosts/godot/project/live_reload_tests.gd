extends Node

# WevaView reloads its files when they change on disk: a CSS edit reapplies
# the stylesheet and keeps the document and its bindings, an HTML edit reloads
# the markup and re-applies the bound data. Files live under user:// so the
# test can rewrite them; modified times have one-second resolution, so each
# edit waits past a second boundary and then two poll intervals.

var checks := 0
var failures := 0
const DIR := "user://live_reload_tests"

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

func write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)
	file.close()

func settle(view: WevaView) -> void:
	# Past the next whole second (so the modified time moves), then two polls:
	# one that notices the change, one that sees it stable and reloads.
	await get_tree().create_timer(1.1).timeout
	await get_tree().create_timer(view.live_reload_interval * 2.5).timeout
	view.update_document(0)

func _ready() -> void:
	var reordered := WevaDocument.new()
	reordered.use_engine_font = false
	reordered.html = "<div id=a>A</div><div id=b>B</div><div id=c>C</div>"
	reordered.css = "body{margin:0} div{height:20px}"
	add_child(reordered)
	reordered.update_document(0)
	reordered.reload_html("<div id=c>C</div><div id=b>B</div><div id=a>A</div>")
	reordered.update_document(0)
	check(reordered.query_all_ids("body > div") == PackedStringArray(["c", "b", "a"]), "reload reverses three keyed siblings")
	check(is_equal_approx(reordered.query_bounds("#c").position.y, 0.0), "first reversed sibling is at the top")
	check(is_equal_approx(reordered.query_bounds("#a").position.y, 40.0), "last reversed sibling is at the bottom")
	reordered.free()

	DirAccess.make_dir_recursive_absolute(DIR)
	write(DIR + "/screen.html", '<div id="a">one</div><span id="name">{{ Name }}</span>')
	write(DIR + "/screen.css", "#a { color: rgb(1, 2, 3); }")
	var reloads: Array = []
	var view := WevaView.new()
	view.html_file = DIR + "/screen.html"
	view.live_reload = true
	view.live_reload_interval = 0.2
	view.document_size = Vector2(300, 200)
	view.use_engine_font = false
	view.files_reloaded.connect(func(markup, stylesheet): reloads.append([markup, stylesheet]))
	add_child(view)
	var model := {"Name": "Morgan"}
	view.bind_state(model)
	view.update_document(0)
	check(view.query_text("#a") == "one" and view.get_computed_style("#a", "color") == "rgb(1, 2, 3)", "files load through the sibling stylesheet")
	check(view.query_text("#name") == "Morgan", "bound data fills the markup")

	await get_tree().create_timer(1.1).timeout
	write(DIR + "/screen.css", "#a { color: rgb(4, 5, 6); }")
	await settle(view)
	check(view.get_computed_style("#a", "color") == "rgb(4, 5, 6)", "a stylesheet edit on disk reapplies the CSS")
	check(reloads == [[false, true]], "and reports a stylesheet-only reload")
	check(view.query_text("#name") == "Morgan", "the document and its bindings survive a stylesheet reload")

	await get_tree().create_timer(1.1).timeout
	write(DIR + "/screen.html", '<div id="a">two</div><div id="b">new</div><span id="name">{{ Name }}</span>')
	await settle(view)
	check(view.query_text("#a") == "two" and view.query_text("#b") == "new", "a markup edit on disk reloads the HTML")
	check(view.query_text("#name") == "Morgan", "bound data is re-applied to the new markup")
	check(view.get_computed_style("#a", "color") == "rgb(4, 5, 6)", "the current stylesheet still applies")
	check(reloads.size() == 2 and reloads[1][0], "and reports a markup reload")

	model.Name = "Jessie"
	view.flush_bindings()
	check(view.query_text("#name") == "Jessie", "later data changes still reach the reloaded markup")

	view.live_reload = false
	await get_tree().create_timer(1.1).timeout
	write(DIR + "/screen.css", "#a { color: rgb(7, 8, 9); }")
	await settle(view)
	check(view.get_computed_style("#a", "color") == "rgb(4, 5, 6)", "live_reload off ignores disk changes")

	view.free()
	print("godot live reload: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures > 0 else 0)
