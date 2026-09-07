class_name WevaView
extends WevaDocument
## File-based UI with signal-driven, coalesced game-state binding.
## Add this node to a scene, choose html_file, then call bind_state in the parent.

signal bindings_refreshed(changed_count: int)

@export_file("*.html") var html_file := ""
@export_file("*.css") var css_file := ""

var last_load_error := ""
var _changes := Signal()
var _refresh_pending := false

func _ready() -> void:
	if not html_file.is_empty() and load_files(html_file, css_file) != OK:
		push_error(last_load_error)

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
	return OK

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
