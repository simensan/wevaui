extends Control

# A browser for the sample corpus: every page the port is measured against,
# rendered live by the C++ engine inside Godot, one at a time.
#
# The other two scenes are for machines. test_scene.tscn asserts on the ABI and
# quits; capture.tscn renders one page to a file and quits. Neither lets you
# LOOK at the thing, which is what this is for.
#
# The samples live outside the project, in the oracle's corpus, because they are
# shared with the layout harness and the Chrome captures sit beside them. They
# are read through absolute paths rather than copied in, so what you see here is
# the same file the gates measure.

const SAMPLES_REL := "../../../tools/oracle/corpus/samples"
# What the corpus is captured at, and therefore what both gates measure.
const GATE_SIZE := Vector2(1280, 720)

var _names: PackedStringArray = []
var _index := 0
var _doc: WevaDocument = null
var _use_engine_font := true
var _full_page := false
var _doc_size := GATE_SIZE
var _pan := 0.0
var _scroll_span := 0.0

@onready var _list: ItemList = %List
@onready var _title: Label = %Title
@onready var _meta: Label = %Meta
@onready var _stage: Control = %Stage


func _samples_dir() -> String:
	# res:// is godot-port/hosts/godot/project, so the port root is three levels
	# up and the corpus hangs off that.
	return ProjectSettings.globalize_path("res://").path_join(SAMPLES_REL).simplify_path()


func _ready() -> void:
	var dir_path := _samples_dir()
	var dir := DirAccess.open(dir_path)
	if dir == null:
		_title.text = "No corpus"
		_meta.text = "Looked in %s" % dir_path
		return

	for f in dir.get_files():
		# Chrome's captures sit beside the sources as <name>.html.chrome.png and
		# <name>.html.chrome-layout.json; only the sources themselves are pages.
		if f.ends_with(".html") and not f.contains(".chrome"):
			_names.append(f.substr(0, f.length() - 5))
	_names.sort()

	for n in _names:
		_list.add_item(n)
	if _names.is_empty():
		_title.text = "No samples found"
		_meta.text = dir_path
		return

	_list.item_selected.connect(_on_selected)
	_stage.resized.connect(_fit)
	_list.select(0)
	_show(0)


func _on_selected(i: int) -> void:
	_show(i)


func _build(size: Vector2) -> void:
	if _doc != null:
		_doc.queue_free()
	_doc = WevaDocument.new()
	_doc.use_engine_font = _use_engine_font
	_doc.document_size = size
	_doc.css = _read(_samples_dir().path_join(_names[_index] + ".css"))
	_doc.html = _read(_samples_dir().path_join(_names[_index] + ".html"))
	_stage.add_child(_doc)
	_doc.update_document()
	_doc_size = size


func _show(i: int) -> void:
	_index = clampi(i, 0, _names.size() - 1)
	_pan = 0.0

	# Laid out at the gate size first, because that is the document the corpus
	# captures and both gates measure -- and because it is what says how far the
	# page reaches.
	_build(GATE_SIZE)
	var reach: float = _doc.get_content_size().y

	# Painting stops at the viewport, so a page taller than it is not merely
	# scrolled off, it is never drawn. Seeing the rest means laying it out in a
	# viewport that fits, and that is a DIFFERENT document -- percentage heights
	# and vh units all move -- so it is a mode you ask for rather than the
	# default. 18 of the 35 samples reach past 720.
	if _full_page and reach > GATE_SIZE.y:
		_build(Vector2(GATE_SIZE.x, ceilf(reach)))

	_fit()

	_title.text = _names[_index]
	var tail := ""
	if _doc_size.y > GATE_SIZE.y:
		tail = "   full page, %dpx -- scroll" % int(_doc_size.y)
	elif reach > GATE_SIZE.y:
		tail = "   page reaches %dpx, P for all of it" % int(reach)
	_meta.text = "%d of %d   %d draws   %d triangles   %s%s" % [
		_index + 1, _names.size(), _doc.get_draw_count(), _doc.get_triangle_count(),
		"engine font" if _use_engine_font else "stub font", tail,
	]
	if _list.get_selected_items().is_empty() or _list.get_selected_items()[0] != _index:
		_list.select(_index)
		_list.ensure_current_is_visible()


func _fit() -> void:
	# ONE TO ONE, and scrolled when it does not fit.
	#
	# The obvious thing is to scale the page into whatever room the window
	# leaves. Do not: the glyph atlas is sampled with nearest filtering, on
	# purpose, because that is what keeps text crisp at 1:1 -- and under any
	# other scale it drops and doubles pixel columns instead. A window 20px
	# wider than the page was enough to put it at 1.015625, which is invisible
	# as a size and ruinous as text: stems came out at different weights within
	# a single word.
	#
	# So the page keeps its own coordinates AND its own pixels. Anything the
	# window cannot show is scrolled to, not shrunk.
	if _doc == null:
		return
	var room := _stage.size
	if room.x <= 0 or room.y <= 0:
		return
	# The one case with no good answer: a window narrower than the page. Scaling
	# down at least shows all of it, and the alternative -- scrolling sideways
	# through a UI -- is worse. Widen the window and it snaps back to 1:1.
	var s := 1.0
	if room.x < _doc_size.x:
		s = room.x / _doc_size.x

	_scroll_span = maxf(0.0, _doc_size.y * s - room.y)
	# Clamped here rather than where the wheel is read, because this also runs
	# on resize and would otherwise restore a pan the new size cannot afford.
	_pan = clampf(_pan, 0.0, _scroll_span)
	var origin := (room - _doc_size * s) * 0.5
	_doc.scale = Vector2(s, s)
	# Floored so the page lands on whole pixels; a half-pixel offset resamples
	# every glyph just as surely as a fractional scale does.
	_doc.position = Vector2(origin.x, minf(origin.y, 0.0) - _pan).floor()


func _read(path: String) -> String:
	# A missing stylesheet is normal: a few samples are a single file.
	var f := FileAccess.open(path, FileAccess.READ)
	return "" if f == null else f.get_as_text()


func _scroll_by(dy: float) -> void:
	# Scrolls whenever there is anything below the fold, which is no longer only
	# full-page mode: at 1:1 a short window has some too.
	if _scroll_span <= 0.0:
		return
	_pan += dy
	_fit()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			_scroll_by(90.0)
			get_viewport().set_input_as_handled()
			return
		if event.button_index == MOUSE_BUTTON_WHEEL_UP:
			_scroll_by(-90.0)
			get_viewport().set_input_as_handled()
			return
	if not (event is InputEventKey) or not event.pressed or event.echo:
		return
	match event.keycode:
		KEY_RIGHT, KEY_DOWN, KEY_SPACE:
			_show(_index + 1 if _index + 1 < _names.size() else 0)
		KEY_LEFT, KEY_UP:
			_show(_index - 1 if _index > 0 else _names.size() - 1)
		KEY_PAGEDOWN:
			_scroll_by(GATE_SIZE.y * 0.9)
		KEY_PAGEUP:
			_scroll_by(-GATE_SIZE.y * 0.9)
		KEY_HOME:
			_show(0)
		KEY_END:
			_show(_names.size() - 1)
		KEY_P:
			_full_page = not _full_page
			_show(_index)
		KEY_F:
			# The stub face is what the comparison harness holds both sides to,
			# so being able to see it is worth a key.
			_use_engine_font = not _use_engine_font
			_show(_index)
		KEY_ESCAPE:
			get_tree().quit()
		_:
			return
	get_viewport().set_input_as_handled()
