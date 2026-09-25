extends SceneTree

# Run with --headless --script res://samples/western_survival/survival_smoke.gd.
# Pointer tests use the sample's actual laid-out button positions.
var checks := 0
var failures := 0
var sample: Control
var doc: WevaDocument

func _initialize() -> void:
	call_deferred("run")

func check(ok: bool, description: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL survival: ",description)

func click(selector: String) -> void:
	var box := doc.query_bounds(selector)
	check(box.size.x > 0 and box.size.y > 0,"click target exists: "+selector)
	var point := box.get_center()
	doc.set_pointer(point,0)
	doc.set_pointer(point,1)
	doc.update_document(0)
	doc.set_pointer(point,0)
	doc.update_document(0)

func key(code: Key, pressed := true) -> void:
	var event := InputEventKey.new()
	event.keycode = code
	event.pressed = pressed
	root.push_input(event,true)
	doc.update_document(0)

func run() -> void:
	root.size = Vector2i(1520,810)
	root.content_scale_size = Vector2i(1520,810)
	sample = load("res://samples/western_survival/survival.tscn").instantiate()
	sample.set_anchors_and_offsets_preset(Control.PRESET_TOP_LEFT)
	sample.size = Vector2(1280,720)
	root.add_child(sample)
	sample.set_process(false)
	doc = sample.get("ui")
	doc.paused = true
	doc.interactive = false
	doc.update_document(0)
	check(doc.document_size == Vector2(1280,720),"720p viewport")
	check(doc.get_missing_assets().is_empty(),"all visible assets resolve")
	check(doc.get_computed_style("#satchel","display") == "none","inventory starts closed")
	click("#open-satchel")
	check(sample.get("inventory_open"),"HUD button opens inventory")
	check(doc.query_all_bounds(".inventory-slot").size() == 24,"24 inventory slots")
	var panel := doc.query_bounds("#satchel")
	for selector in ["#close-satchel","#item-action",".satchel-footer"]:
		check(panel.encloses(doc.query_bounds(selector)),"panel contains "+selector)
	click("#item-2")
	check(doc.get_element_text("#item-name") == "Fresh-water canteen","item selection updates details")
	click("#item-action")
	check(sample.get("thirst") == 63 and sample.call("_item","canteen").count == 2,"drink consumes one water and restores thirst")
	check(doc.get_element_text("#thirst-value") == "63","changed thirst reaches HUD")
	click("#item-action")
	click("#item-action")
	check(sample.get("thirst") == 100 and sample.call("_item","canteen").count == 0,"thirst clamps and final water is consumed")
	check(doc.query_all_bounds("#item-action:disabled").size() == 1,"empty item action is disabled")
	click("#item-4")
	key(KEY_F)
	check(sample.get("health") == 100 and sample.call("_item","bandage").count == 1,"F uses selected item in inventory")
	key(KEY_F)
	check(sample.call("_item","bandage").count == 1,"full health does not waste bandages")
	click("#item-11")
	click("#item-action")
	check(sample.get("equipped_id") == "knife" and doc.get_element_text("#equipped-name") == "TRAIL KNIFE","knife becomes held item")
	check(doc.get_computed_style("#weapon-info","display") == "none","ammo hides when holding a tool")
	click("#tab-crafting")
	check(sample.get("crafting_open"),"craft tab opens recipes")
	click("#craft-action")
	check(sample.call("_item","wood").count == 4 and sample.call("_item","stone").count == 4 and sample.call("_item","campfire").count == 1,"crafting spends materials and creates kit")
	click("#recipe-2")
	click("#craft-action")
	check(sample.call("_item","wood").count == 1 and sample.call("_item","stone").count == 0 and sample.call("_item","hatchet").count == 2,"stone hatchet recipe creates matching item")
	key(KEY_F)
	check(sample.call("_item","hatchet").count == 2 and sample.call("_item","wood").count == 1,"insufficient materials cannot craft via keyboard")
	check(doc.get_missing_assets().is_empty(),"inventory and recipe assets resolve")
	click("#close-satchel")
	check(not sample.get("inventory_open"),"close button closes inventory")
	click("#scavenge")
	key(KEY_E)
	check(sample.call("_item","wood").count == 5 and sample.get("wood_gathered") == 12,"camp supplies are collected only once")
	check(doc.get_computed_style("#scavenge","display") == "none","searched prompt disappears")
	key(KEY_6)
	key(KEY_F)
	check(sample.get("camp_built") and sample.call("_item","campfire").count == 0,"crafted camp kit can be used")
	check(doc.get_element_text("#wood-objective") == "Camp established","objective reflects completed camp")
	click("#hot-0")
	check(sample.get("equipped_id") == "revolver","hotbar responds to pointer")
	key(KEY_SPACE)
	key(KEY_SPACE)
	check(sample.get("loaded") == 4,"fire reduces loaded ammunition")
	key(KEY_R)
	check(sample.get("loaded") == 6 and sample.get("reserve") == 22 and sample.call("_item","ammo").count == 22,"reload transfers reserve ammo into cylinder")
	key(KEY_SHIFT)
	sample.call("_process",1.0)
	check(is_equal_approx(sample.get("stamina"),82.0),"sprint drains stamina")
	key(KEY_SHIFT,false)
	sample.call("_process",1.0)
	check(is_equal_approx(sample.get("stamina"),94.0),"stamina recovers on release")
	key(KEY_TAB)
	check(sample.get("inventory_open"),"Tab opens satchel")
	sample.call("_process",1.0)
	check(is_equal_approx(sample.get("stamina"),94.0),"survival clock pauses in satchel")
	key(KEY_ESCAPE)
	check(not sample.get("inventory_open"),"Escape closes satchel")
	root.remove_child(sample)
	sample.free()
	await verify_gallery()
	print("western survival: %d checks, %d failures" % [checks,failures])
	quit(1 if failures else 0)

func verify_gallery() -> void:
	var gallery = load("res://gallery.tscn").instantiate()
	root.add_child(gallery)
	await process_frame
	await process_frame
	var index: int = gallery.get("_names").find("western-survival")
	check(index >= 0,"gallery lists survival sample")
	if index >= 0:
		gallery.set("_rebuild_every_frame",true)
		gallery.call("_show",index)
		check(gallery.get("_sample_root") != null and not gallery.get("_rebuild_every_frame"),"gallery builds sample without resetting it each frame")
		var scene: Control = gallery.get("_sample_root")
		var document: WevaDocument = gallery.get("_doc")
		check(scene.size == Vector2(1280,720) and document.document_size == scene.size,"gallery keeps world and UI at gate size")
		# Exercise Godot input routing through the scaled gallery Control tree.
		root.notify_mouse_entered()
		var point := document.get_global_transform() * document.query_bounds("#hot-2").get_center()
		var motion := InputEventMouseMotion.new()
		motion.position = point
		motion.global_position = point
		root.push_input(motion,true)
		for pressed in [true,false]:
			var button := InputEventMouseButton.new()
			button.button_index = MOUSE_BUTTON_LEFT
			button.pressed = pressed
			button.position = point
			button.global_position = point
			root.push_input(button,true)
			document.update_document(0)
		check(scene.get("equipped_id") == "canteen","native gallery mouse click selects hotbar item")
		gallery.call("_show",0)
		check(gallery.get("_sample_root") == null and not scene.is_inside_tree(),"leaving sample removes artwork and controller")
		check(gallery.get("_doc") != null,"normal corpus pages still build")
	root.remove_child(gallery)
	gallery.queue_free()
	await process_frame
