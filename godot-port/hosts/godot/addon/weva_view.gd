class_name WevaView
extends WevaDocument
## File-based UI with signal-driven, coalesced game-state binding.
## Add this node to a scene, choose html_file, then call bind_state in the parent.

signal bindings_refreshed(changed_count: int)
## The HTML or CSS file changed on disk and was reloaded (live_reload).
signal files_reloaded(markup_changed: bool, stylesheet_changed: bool)
## The on-screen keyboard opened for the field with this id (controller text entry).
signal keyboard_opened(field_id: String)
## The on-screen keyboard closed; the field keeps whatever was typed.
signal keyboard_closed(field_id: String)

@export_file("*.html") var html_file := ""
@export_file("*.css") var css_file := ""
## Reload the files when they change on disk while the game runs, so a CSS
## or HTML edit shows without a restart. On in debug builds (the editor's F5)
## and off in exported releases by default. A stylesheet edit reapplies the CSS
## and keeps the document, its state and bindings; a markup edit reloads the
## HTML and re-applies the bound data. A file is read once its modified time
## has stayed the same for one interval, so a half-written save is skipped.
@export var live_reload := OS.is_debug_build()
## Seconds between checks of the files' modified times.
@export_range(0.1, 5.0, 0.1) var live_reload_interval := 0.5
## A controller cannot type. With this on, a pad accept on a focused text
## field opens an on-screen keyboard along the bottom of this view: itself an
## HTML document the pad navigates, typing into the field through the same
## text path a physical keyboard uses. Turn it off to answer
## text_entry_requested yourself (a platform keyboard, say).
@export var on_screen_keyboard := true
## Fraction of this view's height the on-screen keyboard covers.
@export_range(0.2, 0.8, 0.05) var on_screen_keyboard_height := 0.42
## Stylesheet for the on-screen keyboard; empty uses the built-in dark theme.
## Key buttons carry class `key`, the wide ones `key wide`, and the focused key
## is `:focus-visible`.
@export_multiline var on_screen_keyboard_css := ""

var last_load_error := ""
var _changes := Signal()
var _refresh_pending := false
var _stylesheet_path := ""
var _watched_times: Dictionary = {}
var _pending_times: Dictionary = {}
var _watch_timer: Timer
var _keyboard: WevaDocument
var _keyboard_field := ""
var _keyboard_shift := false
var _keyboard_symbols := false
var _keyboard_chars: Dictionary = {}

const KEYBOARD_CSS := """
html, body { margin: 0; width: 100%; height: 100%; }
body { display: flex; flex-direction: column; justify-content: center; gap: 6px;
  padding: 10px 12px; box-sizing: border-box; background: #10161bf2; color: #eef2f5;
  font-family: sans-serif; font-size: 18px; border-top: 1px solid #3b4a56; }
.row { display: flex; justify-content: center; gap: 6px; }
.key { flex: 1 1 0; max-width: 72px; height: 44px; padding: 0; border: 1px solid #3b4a56;
  border-radius: 6px; background: #1f2a33; color: inherit; font: inherit; cursor: pointer; }
.key.wide { flex: 3 1 0; max-width: none; font-size: 15px; }
.key:hover { background: #2c3a46; }
.key:focus-visible { outline: 3px solid #f2c76b; outline-offset: -1px; background: #33465a; }
.key.on { background: #3d5670; border-color: #8fb4d6; }
"""

func _ready() -> void:
	if not html_file.is_empty() and load_files(html_file, css_file) != OK:
		push_error(last_load_error)
	_watch_timer = Timer.new()
	_watch_timer.wait_time = live_reload_interval
	_watch_timer.timeout.connect(_check_files)
	add_child(_watch_timer)
	_watch_timer.start()
	text_entry_requested.connect(_on_text_entry_requested)

## Reads both files before changing the document. Images resolve beside the HTML.
## With no CSS path, uses a same-name .css file when present, or no stylesheet.
func load_files(markup_path: String, stylesheet_path := "") -> Error:
	last_load_error = ""
	var markup := FileAccess.open(markup_path, FileAccess.READ)
	if markup == null:
		var error := FileAccess.get_open_error()
		last_load_error = "WevaView could not read HTML: " + markup_path
		return error
	var stylesheet := stylesheet_path
	if stylesheet.is_empty():
		var sibling := markup_path.get_basename() + ".css"
		if FileAccess.file_exists(sibling):
			stylesheet = sibling
	var styles := ""
	if not stylesheet.is_empty():
		var file := FileAccess.open(stylesheet, FileAccess.READ)
		if file == null:
			var error := FileAccess.get_open_error()
			last_load_error = "WevaView could not read CSS: " + stylesheet
			return error
		styles = file.get_as_text()
	base_path = markup_path.get_base_dir()
	css = styles
	html = markup.get_as_text()
	html_file = markup_path
	css_file = stylesheet_path
	_stylesheet_path = stylesheet
	_remember_times()
	return OK

func _remember_times() -> void:
	_watched_times.clear()
	_pending_times.clear()
	for path in [html_file, _stylesheet_path]:
		if not path.is_empty():
			_watched_times[path] = FileAccess.get_modified_time(path)

func _check_files() -> void:
	if _watch_timer.wait_time != live_reload_interval:
		_watch_timer.wait_time = live_reload_interval
	if not live_reload or html_file.is_empty() or _watched_times.is_empty():
		return
	var markup_changed := false
	var stylesheet_changed := false
	for path in _watched_times.keys():
		var now: int = FileAccess.get_modified_time(path)
		if now == 0 or now == _watched_times[path]:
			_pending_times.erase(path)
			continue
		# Wait for the modified time to stop moving before reading.
		if _pending_times.get(path, -1) != now:
			_pending_times[path] = now
			continue
		if path == html_file:
			markup_changed = true
		else:
			stylesheet_changed = true
	if not markup_changed and not stylesheet_changed:
		return
	if markup_changed:
		if load_files(html_file, css_file) != OK:
			push_warning(last_load_error)
			_remember_times()
			return
	else:
		var file := FileAccess.open(_stylesheet_path, FileAccess.READ)
		if file == null:
			push_warning("WevaView could not reread CSS: " + _stylesheet_path)
			_remember_times()
			return
		css = file.get_as_text()
		_remember_times()
	files_reloaded.emit(markup_changed, stylesheet_changed)

## The dictionary is shared, so data-model edits write into the game's state.
## changes must be a zero-argument signal; emit it after game-side mutations.
## Rebinding disconnects the previous source. No polling or extra process loop.
func bind_state(model: Dictionary, actions: Object = null, changes: Signal = Signal()) -> void:
	if not _changes.is_null() and is_instance_valid(_changes.get_object()):
		if _changes.is_connected(request_refresh):
			_changes.disconnect(request_refresh)
	_changes = changes
	_refresh_pending = false
	set_controller(actions)
	data = model
	if not _changes.is_null():
		_changes.connect(request_refresh)

## Multiple changes in one game tick refresh once after the current callback.
func request_refresh() -> void:
	if _refresh_pending:
		return
	_refresh_pending = true
	_refresh_if_pending.call_deferred()

## Use before an immediate geometry read; normal gameplay needs only the signal.
func flush_bindings() -> int:
	_refresh_pending = false
	var count := refresh_bindings()
	bindings_refreshed.emit(count)
	return count

func _refresh_if_pending() -> void:
	if _refresh_pending:
		flush_bindings()

# --- On-screen keyboard ------------------------------------------------------

## The keyboard document, once it has been shown; null before that.
func get_keyboard() -> WevaDocument:
	return _keyboard

## Whether the on-screen keyboard is currently open.
func is_keyboard_open() -> bool:
	return _keyboard != null and _keyboard.visible

func _on_text_entry_requested(field_id: String) -> void:
	if on_screen_keyboard:
		open_keyboard(field_id)

## Opens the on-screen keyboard for the focused field (called for you on a
## pad accept; a game may call it from a touch or mouse handler too).
func open_keyboard(field_id := "") -> void:
	_keyboard_field = field_id
	if _keyboard == null:
		_keyboard = WevaDocument.new()
		_keyboard.name = "OnScreenKeyboard"
		_keyboard.use_engine_font = use_engine_font
		_keyboard.gamepad_wake = false
		_keyboard.gamepad_text_entry = false
		_keyboard.element_clicked.connect(_on_keyboard_key)
		add_child(_keyboard)
	_keyboard.css = KEYBOARD_CSS if on_screen_keyboard_css.is_empty() else on_screen_keyboard_css
	_keyboard.set_anchors_preset(Control.PRESET_BOTTOM_WIDE)
	_keyboard.anchor_top = 1.0 - on_screen_keyboard_height
	_keyboard.offset_top = 0
	_keyboard.offset_bottom = 0
	_keyboard.offset_left = 0
	_keyboard.offset_right = 0
	_keyboard_shift = false
	_keyboard_symbols = false
	_render_keyboard()
	_keyboard.show()
	# The field keeps its focus and caret while the keyboard owns the pad.
	retain_html_focus = true
	_keyboard.grab_focus()
	_keyboard.set_focus("#k-q")
	keyboard_opened.emit(field_id)

## Closes the on-screen keyboard, returning the pad to this view with the
## field still focused.
func close_keyboard() -> void:
	if not is_keyboard_open():
		return
	_keyboard.hide()
	grab_focus()
	retain_html_focus = false
	keyboard_closed.emit(_keyboard_field)

func _keyboard_rows() -> Array:
	if _keyboard_symbols:
		return ["1234567890", "!@#$%&*()-", "_+=/:;'\",.", "?<>[]{}|~^"]
	return ["1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm"]

func _render_keyboard() -> void:
	_keyboard_chars.clear()
	var markup := ""
	for row in _keyboard_rows():
		markup += '<div class="row">'
		for i in row.length():
			var ch: String = row[i]
			var shown := ch.to_upper() if _keyboard_shift else ch
			var id := "k-" + ch if ch.is_valid_identifier() or ch.is_valid_int() else "k-c%d" % ch.unicode_at(0)
			_keyboard_chars[id] = shown
			markup += '<button class="key" id="%s">%s</button>' % [id, shown.xml_escape()]
		markup += '</div>'
	markup += '<div class="row">'
	markup += '<button class="key wide%s" id="k-shift">Shift</button>' % (" on" if _keyboard_shift else "")
	markup += '<button class="key wide%s" id="k-sym">%s</button>' % [" on" if _keyboard_symbols else "", "abc" if _keyboard_symbols else "?123"]
	markup += '<button class="key wide" id="k-space">Space</button>'
	markup += '<button class="key wide" id="k-back">Back</button>'
	markup += '<button class="key wide" id="k-enter">Enter</button>'
	markup += '<button class="key wide" id="k-done">Done</button>'
	markup += '</div>'
	_keyboard.html = markup
	_keyboard.update_document(0)

func _on_keyboard_key(id: String) -> void:
	match id:
		"k-shift":
			_keyboard_shift = not _keyboard_shift
			_render_keyboard()
			_keyboard.set_focus("#k-shift")
		"k-sym":
			_keyboard_symbols = not _keyboard_symbols
			_keyboard_shift = false
			_render_keyboard()
			_keyboard.set_focus("#k-sym")
		"k-space":
			send_text(" ")
		"k-back":
			send_key(KEY_BACKSPACE, true)
			send_key(KEY_BACKSPACE, false)
		"k-enter":
			# Enter as the field would see it: a newline in a textarea, an
			# implicit submission in a form field; then the keyboard goes away.
			send_key(KEY_ENTER, true)
			send_key(KEY_ENTER, false)
			close_keyboard()
		"k-done":
			close_keyboard()
		_:
			if _keyboard_chars.has(id):
				send_text(_keyboard_chars[id])
				if _keyboard_shift:
					_keyboard_shift = false
					_render_keyboard()
					_keyboard.set_focus("#" + id)

func _unhandled_input(event: InputEvent) -> void:
	# The keyboard document consumes cancel only when it closed something of
	# its own, so a cancel while it is open reaches here: close the keyboard.
	if not is_keyboard_open():
		return
	var cancel := event.is_action_pressed("ui_cancel")
	if event is InputEventJoypadButton and event.pressed and event.button_index == JOY_BUTTON_B:
		cancel = true
	if cancel:
		close_keyboard()
		get_viewport().set_input_as_handled()
