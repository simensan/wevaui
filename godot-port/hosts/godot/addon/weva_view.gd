class_name WevaView
extends WevaDocument
## File-based UI with signal-driven, coalesced game-state binding.
## Add this node to a scene, choose html_file, then call bind_state in the parent.

signal bindings_refreshed(changed_count: int)
## The HTML or CSS file changed on disk and was reloaded (live_reload).
signal files_reloaded(markup_changed: bool, stylesheet_changed: bool)

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

var last_load_error := ""
var _changes := Signal()
var _refresh_pending := false
var _stylesheet_path := ""
var _watched_times: Dictionary = {}
var _pending_times: Dictionary = {}
var _watch_timer: Timer

func _ready() -> void:
	if not html_file.is_empty() and load_files(html_file, css_file) != OK:
		push_error(last_load_error)
	_watch_timer = Timer.new()
	_watch_timer.wait_time = live_reload_interval
	_watch_timer.timeout.connect(_check_files)
	add_child(_watch_timer)
	_watch_timer.start()

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
