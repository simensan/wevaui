extends RefCounted
## Drives native Godot viewport events through the real main scene.

var checks := 0
var failures := 0
var game: Control
var ui: WevaView
var viewport: Viewport
var tree: SceneTree

func check(ok: bool, label: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL frontier: " + label)

func settle() -> void:
	await tree.process_frame
	await tree.process_frame
	ui.update_document(0)

func click_at(point: Vector2) -> void:
	var motion := InputEventMouseMotion.new()
	motion.position = point
	motion.global_position = point
	viewport.push_input(motion, true)
	for down in [true, false]:
		var event := InputEventMouseButton.new()
		event.position = point
		event.global_position = point
		event.button_index = MOUSE_BUTTON_LEFT
		event.button_mask = MOUSE_BUTTON_MASK_LEFT if down else 0
		event.pressed = down
		viewport.push_input(event, true)
	await settle()

func click(selector: String) -> void:
	var box := ui.query_bounds(selector)
	check(box.has_area(), "click target exists: " + selector)
	await click_at(box.get_center())

func key(code: Key, unicode := 0, ctrl := false, down := true) -> void:
	var event := InputEventKey.new()
	event.keycode = code
	event.unicode = unicode
	event.ctrl_pressed = ctrl
	event.pressed = down
	viewport.push_input(event, true)

func capture(name: String) -> void:
	if game.capture_prefix.is_empty() or DisplayServer.get_name() == "headless":
		return
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	check(image != null and image.get_width() > 0, "native renderer produced " + name)
	check(image.save_png(game.capture_prefix + "-" + name + ".png") == OK, "saved " + name)

func run(scene: Control) -> void:
	game = scene
	ui = game.ui
	viewport = game.get_viewport()
	tree = game.get_tree()
	game.get_node("Clock").stop()
	viewport.notify_mouse_entered()
	await settle()
	check(ui.last_load_error.is_empty(), "Inspector loads HTML and sibling CSS")
	check(ui.document_size == Vector2(1280, 720), "native Control owns the viewport")
	check(ui.get_draw_count() > 0, "document draws")
	check(ui.get_missing_assets().is_empty(), "HTML-relative icons and native background resolve")
	check(ui.query_text("#player-label") == "Morgan", "initial bound name")
	check(ui.query_text("#health") == "72", "initial bound health")
	check(ui.count_elements("#inventory > .item") == 4, "data-each builds four inventory rows")
	check(ui.query_bounds("#health-bar").size.x > 0, "style binding fills health meter")
	await capture("camp")

	# Deferred refresh coalesces a burst, and idle frames do not poll bindings.
	var refreshes := [0]
	var on_refresh := func(_count: int): refreshes[0] += 1
	ui.bindings_refreshed.connect(on_refresh)
	game.state.model.Player.Health = 68
	game.state.publish()
	game.state.model.Player.Health = 61
	game.state.publish()
	game.state.publish()
	await settle()
	check(refreshes[0] == 1 and ui.query_text("#health") == "61", "three state notifications refresh once with final data")
	check(is_equal_approx(ui.query_bounds("#health-bar").size.x, ui.query_bounds(".vital .meter").size.x * 0.61), "numeric state updates a CSS width binding")
	await settle()
	check(refreshes[0] == 1, "idle frames do not refresh bindings")
	ui.bindings_refreshed.disconnect(on_refresh)

	var html_before: String = ui.html
	var css_before: String = ui.css
	check(ui.load_files("res://ui/does-not-exist.html") != OK, "missing HTML returns a useful error")
	check(ui.last_load_error.contains("does-not-exist.html"), "load error names the missing file")
	check(ui.html == html_before and ui.css == css_before, "failed HTML load preserves the current UI")
	check(ui.load_files("res://ui/camp.html", "res://ui/missing.css") != OK, "missing explicit CSS returns an error")
	check(ui.html == html_before and ui.css == css_before, "failed CSS load preserves the current UI")

	await click("#craft")
	check(game.state.count("campfire") == 0, "disabled craft cannot spend unavailable materials")
	check(game.world_actions == 0, "disabled UI click does not leak into the game")
	await click("#forage")
	check(game.state.count("wood") == 6 and ui.query_text("#wood-cost") == "6 / 4", "HTML action updates game inventory and recipe binding")
	check(game.state.model.Player.Gold == 19 and ui.query_text("#gold") == "19", "same action updates the HUD")
	check(game.world_actions == 0, "UI button triggers its action exactly once without gameplay leakage")
	check(not ui.has_element_attribute("#craft", "disabled"), "craft unlocks from a boolean binding")
	await click("#craft")
	check(game.state.count("wood") == 2 and game.state.count("stone") == 0, "craft consumes exact materials")
	check(game.state.count("campfire") == 1 and ui.has_element("#use-campfire"), "craft adds a new repeated row and handler")
	check(not ui.has_element("#item-stone"), "depleted stack removes its row")
	await click("#sort")
	check(ui.query_all_ids("#inventory > .item")[0] == "item-campfire", "keyed rows reorder from game data")
	check(ui.get_row("#use-campfire").get("key", "") == "campfire", "reordered button retains stable item identity")
	await capture("crafted")
	await click("#use-campfire")
	check(game.state.model.View.CampBuilt and game.state.count("campfire") == 0, "new reordered button places the correct item")
	check(not ui.has_element("#item-campfire") and ui.query_text("#objective").contains("Camp established"), "consuming the clicked row removes it and updates the objective")
	await click("#use-bandage")
	check(game.state.model.Player.Health == 81 and game.state.count("bandage") == 1, "row action after removal still resolves the correct item")
	await click("#use-bandage")
	check(game.state.model.Player.Health == 100 and not ui.has_element("#item-bandage"), "last bandage clamps health and removes its row")
	check(ui.get_missing_assets().is_empty(), "newly bound item icons resolve")

	# Real keyboard activation uses the same HTML controller action.
	ui.set_focus("#forage")
	var wood_before: int = game.state.count("wood")
	key(KEY_SPACE)
	check(game.state.count("wood") == wood_before, "Space press waits for release")
	key(KEY_SPACE, 0, false, false)
	await settle()
	check(game.state.count("wood") == wood_before + 3, "Space release invokes bound action once")
	await click_at(Vector2(640, 606))
	check(game.world_actions == 1, "transparent HUD ground passes a native click to gameplay")
	ui.set_focus("")
	key(KEY_H)
	await settle()
	check(game.state.model.Player.Health == 85 and ui.query_text("#health") == "85", "game-side keyboard damage reaches bindings")

	await click("#settings-button")
	check(game.settings_open and ui.has_element_attribute("#settings", "open"), "HTML action opens a modal dialog")
	await click("#player-name")
	key(KEY_A, 97, true)
	for character in "Jessie":
		key(KEY_NONE, character.unicode_at(0))
	await settle()
	check(game.state.model.Player.Name == "Jessie", "native typing writes back through data-model")
	check(ui.query_text("#player-label") == "Jessie", "two-way edit updates another bound element")
	check(game.world_actions == 1, "typing E inside a field does not forage")
	await click("#volume")
	key(KEY_END)
	key(KEY_LEFT)
	await settle()
	check(game.state.model.Settings.Volume is int and game.state.model.Settings.Volume == 99, "range keyboard edits preserve an integer model")
	check(ui.query_text("#volume-label") == "99%", "range value updates its bound label")
	check(is_equal_approx(db_to_linear(AudioServer.get_bus_volume_db(0)), 0.99), "two-way setting is applied to native Godot audio")
	await click("#music")
	check(game.state.model.Settings.Music is bool and not game.state.model.Settings.Music, "checkbox writes a boolean")
	check(AudioServer.is_bus_mute(0), "checkbox applies to native audio mute")
	await capture("settings")
	await click_at(Vector2(640, 606))
	check(game.world_actions == 1, "modal backdrop blocks gameplay clicks")
	await click("#reset-settings")
	check(game.state.model.Player.Name == "Morgan" and game.state.model.Settings.Volume == 65 and game.state.model.Settings.Music, "HTML reset restores name, volume and checkbox models")
	check(ui.query_text("#player-label") == "Morgan" and not AudioServer.is_bus_mute(0), "form reset updates bound HUD and native settings")
	key(KEY_ESCAPE)
	await settle()
	check(not game.settings_open and not ui.has_element_attribute("#settings", "open"), "Escape closes the modal")
	check(ui.get_focused_id() == "settings-button", "closing returns keyboard focus to its trigger")
	await click("#settings-button")
	await click("#close-settings")
	check(not game.settings_open, "bound close button also works")

	# Changes from a real native Timer need no UI update loop in game.gd.
	var clock_before: String = ui.query_text("#clock")
	game.get_node("Clock").start(0.04)
	await tree.create_timer(0.12).timeout
	game.get_node("Clock").stop()
	await settle()
	check(ui.query_text("#clock") != clock_before, "native Timer updates bound clock text")
	await helper_lifecycle()

	for dimensions in [Vector2(1024, 720), Vector2(1600, 900), Vector2(1280, 720)]:
		game.size = dimensions
		await settle()
		check(ui.document_size == dimensions, "Control resize updates document: " + str(dimensions))
		var bounds := Rect2(Vector2.ZERO, dimensions)
		for selector in [".satchel", ".journal", ".hud", "#settings-button", "#forage"]:
			check(bounds.encloses(ui.query_bounds(selector)), "viewport contains " + selector + " at " + str(dimensions))
	check(ui.get_missing_assets().is_empty(), "all assets remain resolved")
	print("frontier integration: %d checks, %d failures" % [checks, failures])
	tree.quit(1 if failures else 0)

func helper_lifecycle() -> void:
	var probe := WevaView.new()
	probe.html = "<p id='value'>{{ Player.Health }}</p>"
	probe.visible = false
	game.add_child(probe)
	var first = game.CampState.new()
	var second = game.CampState.new()
	probe.bind_state(first.model, null, first.changed)
	first.publish()
	probe.bind_state(second.model, null, second.changed)
	var refreshes := [0]
	probe.bindings_refreshed.connect(func(_count): refreshes[0] += 1)
	first.model.Player.Health = 11
	first.publish()
	await settle()
	check(refreshes[0] == 0, "rebinding disconnects the previous source")
	second.model.Player.Health = 33
	second.publish()
	await settle()
	check(refreshes[0] == 1 and probe.query_text("#value") == "33", "new source continues to refresh")
	second.publish()
	probe.flush_bindings()
	await settle()
	check(refreshes[0] == 2, "immediate flush cancels its queued refresh")
	second.publish()
	probe.free()
	await settle()
	check(second.changed.get_connections().is_empty(), "freeing a view disconnects its signal and cancels pending work")
