## Chrome comparison fixture; run through run_frontier_chrome.py.
extends SceneTree
var game
var ui
var view: SubViewport
var driver
var snapshots: Array = []
var selectors := [".camp", ".masthead", ".satchel", ".panel-heading", "#inventory", ".satchel-footer", ".journal", ".recipe", ".hud", ".identity", ".vital", ".world-hint", "#settings", "#settings-form", "#player-name", "#volume", "#music", "#close-settings", "#settings-button", "#item-beans", "#item-stone", "#use-beans", "#sort", "#craft", "#forage", "#health", "#health-bar", "#player-label", "#volume-label"]
func _initialize() -> void:
	call_deferred("run")
func snap(label: String) -> void:
	await driver.settle()
	var rects := {}
	for selector in selectors:
		var r: Rect2 = ui.query_bounds(selector)
		rects[selector] = [r.position.x, r.position.y, r.size.x, r.size.y]
	var s: Vector2 = ui.get_element_scroll("#inventory")
	snapshots.append({"name":label,"size":[view.size.x,view.size.y],"rects":rects,"focus":ui.get_focused_id(),"scroll":[s.x,s.y],"value":ui.get_element_value("#player-name"),"model":game.state.model.duplicate(true)})
	if DisplayServer.get_name() != "headless":
		await RenderingServer.frame_post_draw
		view.get_texture().get_image().save_png("res://native-"+label+".png")
func run() -> void:
	view = SubViewport.new()
	view.size = Vector2i(1280,720)
	view.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var container := SubViewportContainer.new()
	root.add_child(container)
	container.add_child(view)
	game = load("res://main.tscn").instantiate()
	view.add_child(game)
	game.get_node("Clock").stop()
	ui = game.ui
	driver = load("res://tests/integration.gd").new()
	driver.game = game
	driver.ui = ui
	driver.viewport = view
	driver.tree = self
	view.notify_mouse_entered()
	var font = ui.get_theme_font("font")
	if font is FontFile:
		var f = FileAccess.open("res://chrome-font.ttf",FileAccess.WRITE)
		f.store_buffer(font.data)
	await snap("closed")
	await driver.click("#settings-button")
	await snap("opened")
	driver.key(KEY_TAB)
	await snap("tab")
	ui.set_focus("#player-name")
	driver.key(KEY_A,0,true)
	driver.key(KEY_R,82)
	await snap("typed")
	await driver.click("#close-settings")
	await snap("closed-again")
	# Add real keyed rows to make the inventory overflow; same data goes to Chrome.
	for i in range(12):
		var row: Dictionary = game.state.model.Items[0].duplicate()
		row.Id = "extra%d" % i
		game.state.model.Items.append(row)
	game.state.publish()
	await driver.settle()
	ui.set_element_scroll("#inventory",Vector2(0,120))
	await snap("scrolled")
	await driver.click("#settings-button")
	await snap("scrolled-open")
	for size in [Vector2i(1024,720),Vector2i(1920,1080),Vector2i(3840,2160)]:
		view.size = size
		await snap("resize-%d" % size.x)
	await driver.click("#close-settings")
	await snap("resize-closed")
	var out = FileAccess.open("res://native.json",FileAccess.WRITE)
	out.store_string(JSON.stringify({"states":snapshots,"selectors":selectors,"checks":driver.checks,"failures":driver.failures},"  "))
	game.queue_free()
	await process_frame
	quit(driver.failures)
