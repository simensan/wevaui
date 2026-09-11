extends Control

# Sample game state, deliberately separate from layout and artwork. The world
# is a native static TextureRect; only changed UI values reach Weva each tick.
const BASE := "res://samples/western_survival"
const HOT_ITEMS := ["revolver", "hatchet", "canteen", "beans", "bandage", "campfire"]
const RECIPES := [
	{"name":"Campfire","icon":"campfire","result":"campfire","description":"Turn a few supplies into a safe place for the night.","cost":{"wood":4,"stone":2}},
	{"name":"Clean bandage","icon":"bandage","result":"bandage","description":"Clean cloth, tightly wrapped. Restores 20 health when used.","cost":{"cloth":2}},
	{"name":"Stone hatchet","icon":"hatchet","result":"hatchet","description":"A rough edge for a rough country. Keep a spare on the trail.","cost":{"wood":3,"stone":4}}
]

var ui: WevaDocument
var items: Array[Dictionary] = [
	{"id":"revolver","name":"Frontier revolver","category":"EQUIPMENT / WEAPON","count":1,"weight":1.2,"text":"Six chances to make it home. A reliable single-action revolver, worn smooth from years on the trail."},
	{"id":"hatchet","name":"Stone hatchet","category":"EQUIPMENT / TOOL","count":1,"weight":1.6,"text":"A chipped stone edge lashed to a hickory handle. Useful for firewood and whatever else the trail brings."},
	{"id":"canteen","name":"Fresh-water canteen","category":"PROVISIONS / WATER","count":3,"weight":0.8,"text":"Cool, clean water. A few mouthfuls restore 25 thirst. Out here, this is worth more than silver."},
	{"id":"beans","name":"Baked beans","category":"PROVISIONS / FOOD","count":2,"weight":0.4,"text":"A meal you can carry. Eat one tin to restore 25 hunger. Better warm, but no one is judging."},
	{"id":"bandage","name":"Clean bandage","category":"PROVISIONS / MEDICINE","count":2,"weight":0.1,"text":"Keep it clean and keep it close. Use a bandage to restore 20 health."},
	{"id":"campfire","name":"Campfire kit","category":"CAMP / SHELTER","count":0,"weight":1.0,"text":"A little warmth against a very big night. Craft a kit, then use it to establish your camp."},
	{"id":"wood","name":"Dry wood","category":"MATERIAL / COMMON","count":8,"weight":0.5,"text":"Seasoned branches collected along the creek. Used to craft campfires and tools."},
	{"id":"stone","name":"River stone","category":"MATERIAL / COMMON","count":6,"weight":0.3,"text":"Worn smooth by the water. A useful weight, a rough cutting edge, or the start of a hearth."},
	{"id":"cloth","name":"Cloth scraps","category":"MATERIAL / COMMON","count":6,"weight":0.1,"text":"Clean enough to wrap a wound. Two scraps make one bandage."},
	{"id":"rope","name":"Hemp rope","category":"MATERIAL / UNCOMMON","count":2,"weight":0.4,"text":"A length of good rope. Holds the camp together when nothing else will."},
	{"id":"ammo","name":".45 cartridges","category":"AMMUNITION / PISTOL","count":24,"weight":0.02,"text":"Brass cases, lead bullets. Reserve ammunition for your frontier revolver."},
	{"id":"knife","name":"Trail knife","category":"EQUIPMENT / TOOL","count":1,"weight":0.3,"text":"Small, sharp, indispensable. A trusty companion for the daily work of staying alive."}
]
var health := 82
var hunger := 64
var thirst := 38
var stamina := 100.0
var loaded := 6
var reserve := 24
var selected_item := 0
var selected_hotbar := 0
var equipped_id := "revolver"
var selected_recipe := 0
var inventory_open := false
var crafting_open := false
var camp_searched := false
var camp_built := false
var wood_gathered := 8
var sprinting := false
var _tick := 0.0
var _toast_time := 0.0
var _shown_stamina := -1

func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	var world := TextureRect.new()
	world.texture = load(BASE + "/assets/frontier.png")
	world.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	world.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_COVERED
	world.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(world)
	world.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	# A static tint guarantees readable fine text over bright sky. No blur or
	# full-screen gradient texture is rebuilt when a survival meter changes.
	var shade := ColorRect.new()
	shade.color = Color(0.035,0.055,0.035,0.16)
	shade.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(shade)
	shade.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	var sky_shade := TextureRect.new()
	var gradient := Gradient.new()
	gradient.colors = PackedColorArray([Color(0.025,0.035,0.025,0.60),Color(0.025,0.035,0.025,0)])
	var sky_texture := GradientTexture2D.new()
	sky_texture.gradient = gradient
	sky_texture.fill_from = Vector2(0,0)
	sky_texture.fill_to = Vector2(0,1)
	sky_texture.width = 2
	sky_texture.height = 256
	sky_shade.texture = sky_texture
	sky_shade.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	sky_shade.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(sky_shade)
	sky_shade.set_anchors_and_offsets_preset(Control.PRESET_TOP_WIDE)
	sky_shade.offset_bottom = 330
	ui = WevaDocument.new()
	ui.base_path = BASE
	ui.css = FileAccess.get_file_as_string(BASE + "/survival.css")
	ui.html = FileAccess.get_file_as_string(BASE + "/survival.html")
	ui.controller = self
	add_child(ui)
	ui.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_build_inventory()
	_render_all()
	ui.update_document(0)

func _build_inventory() -> void:
	var markup := ""
	for i in 24:
		if i < items.size():
			var item := items[i]
			markup += '<button id="item-%d" class="inventory-slot" on-click="select_inventory_item" title="%s"><img src="assets/%s.svg" alt="%s"><span id="count-%d" class="stack">%d</span></button>' % [i,item.name,item.id,item.name,i,item.count]
		else:
			markup += '<button class="inventory-slot empty" disabled aria-label="Empty inventory slot">+</button>'
	ui.set_element_html("#inventory-grid",markup)

func _item(item_id: String) -> Dictionary:
	for item in items:
		if item.id == item_id:
			return item
	return {}

func _render_vitals() -> void:
	for vital in ["health","hunger","thirst"]:
		var value: int = get(vital)
		ui.set_element_text("#"+vital+"-value",str(value))
		ui.set_element_style("#"+vital+"-fill","width","%d%%" % value)
	ui.set_element_text("#condition","NEED A MOMENT" if health < 35 else "HOLDING UP")
	ui.set_element_text("#vitals-note","Find water before the trail gets longer." if thirst < 40 else "A little better prepared for the road ahead.")
	_render_stamina()

func _render_stamina() -> void:
	var value := int(stamina)
	if value == _shown_stamina:
		return
	_shown_stamina = value
	ui.set_element_text("#stamina-value",str(value))
	ui.set_element_style("#stamina-fill","width","%d%%" % value)

func _render_all() -> void:
	_render_vitals()
	var weight := 0.0
	for i in items.size():
		var item := items[i]
		weight += float(item.weight) * int(item.count)
		ui.set_element_text("#count-%d" % i,str(item.count))
		ui.toggle_element_class("#item-%d" % i,"chosen",i == selected_item)
	ui.set_element_text("#carry-weight","%.1f" % weight)
	for i in HOT_ITEMS.size():
		ui.toggle_element_class("#hot-%d" % i,"selected",i == selected_hotbar)
		if i >= 2:
			ui.set_element_text("#hot-count-%d" % i,str(_item(HOT_ITEMS[i]).count))
	_render_equipped()
	ui.set_element_text("#loaded","%02d" % loaded)
	ui.set_element_text("#reserve","%02d" % reserve)
	ui.set_element_text("#wood-count","%d / 12" % mini(wood_gathered,12))
	ui.set_element_text("#objective","Your fire is lit. You have a place for the night." if camp_built else "Gather supplies and build a campfire.")
	ui.set_element_text("#wood-objective","Camp established" if camp_built else "Gather wood")
	ui.toggle_element_class("#scavenge","hidden",camp_searched or inventory_open)
	_render_detail()
	_render_recipe()

func _render_detail() -> void:
	var item := items[selected_item]
	ui.set_element_text("#item-name",item.name)
	ui.set_element_text("#item-category",item.category)
	ui.set_element_text("#item-description",item.text)
	ui.set_element_text("#item-weight","%.1f kg" % item.weight)
	ui.set_element_text("#item-quantity",str(item.count))
	ui.set_element_attribute("#detail-icon","src","assets/%s.svg" % item.id)
	var actions := {"canteen":"DRINK WATER","beans":"EAT PROVISIONS","bandage":"USE BANDAGE","campfire":"ESTABLISH CAMP"}
	var equippable: bool = HOT_ITEMS.has(item.id) or item.id == "knife"
	var label: String = actions.get(item.id,"EQUIP ITEM" if equippable else "CRAFT WITH THIS")
	ui.set_element_text("#item-action",label)
	if int(item.count) == 0:
		ui.set_element_attribute("#item-action","disabled","")
	else:
		ui.remove_element_attribute("#item-action","disabled")

func _render_recipe() -> void:
	var recipe: Dictionary = RECIPES[selected_recipe]
	ui.set_element_text("#recipe-name",recipe.name)
	ui.set_element_text("#recipe-description",recipe.description)
	ui.set_element_attribute("#recipe-icon","src","assets/%s.svg" % recipe.icon)
	var markup := ""
	var available := true
	for id: String in recipe.cost:
		var have := int(_item(id).count)
		var need := int(recipe.cost[id])
		available = available and have >= need
		markup += '<div class="material%s"><img src="assets/%s.svg" alt=""><span>%s</span><strong>%d / %d</strong></div>' % [" short" if have < need else "",id,_item(id).name,have,need]
	ui.set_element_html("#recipe-costs",markup)
	ui.set_element_text("#craft-action","CRAFT " + str(recipe.name).to_upper())
	ui.set_element_text("#craft-status","All materials available." if available else "You need more supplies for this recipe.")
	if available:
		ui.remove_element_attribute("#craft-action","disabled")
	else:
		ui.set_element_attribute("#craft-action","disabled","")
	for i in RECIPES.size():
		ui.toggle_element_class("#recipe-%d" % i,"chosen",i == selected_recipe)

func notify(message: String) -> void:
	ui.set_element_text("#toast",message)
	ui.remove_element_class("#toast","hidden")
	_toast_time = 3.0

func open_inventory(_id: String = "") -> void:
	inventory_open = true
	ui.add_element_class("#survival","menu-open")
	ui.remove_element_class("#scrim","hidden")
	ui.remove_element_class("#satchel","hidden")
	ui.add_element_class("#scavenge","hidden")
	ui.add_element_class("#crosshair","hidden")
	show_inventory()
	ui.set_focus("#tab-inventory")

func close_inventory(_id: String = "") -> void:
	inventory_open = false
	ui.remove_element_class("#survival","menu-open")
	ui.add_element_class("#scrim","hidden")
	ui.add_element_class("#satchel","hidden")
	ui.remove_element_class("#crosshair","hidden")
	ui.toggle_element_class("#scavenge","hidden",camp_searched)
	ui.set_focus("")

func show_inventory(_id: String = "") -> void:
	crafting_open = false
	ui.remove_element_class("#satchel","craft-mode")
	ui.add_element_class("#tab-inventory","active")
	ui.remove_element_class("#tab-crafting","active")

func show_crafting(_id: String = "") -> void:
	if not inventory_open:
		open_inventory()
	crafting_open = true
	ui.add_element_class("#satchel","craft-mode")
	ui.remove_element_class("#tab-inventory","active")
	ui.add_element_class("#tab-crafting","active")
	ui.set_focus("#tab-crafting")
	_render_recipe()

func select_inventory_item(id: String) -> void:
	selected_item = clampi(id.trim_prefix("item-").to_int(),0,items.size()-1)
	for i in items.size():
		ui.toggle_element_class("#item-%d" % i,"chosen",i == selected_item)
	_render_detail()

func select_hotbar(id: String) -> void:
	selected_hotbar = clampi(id.trim_prefix("hot-").to_int(),0,HOT_ITEMS.size()-1)
	equipped_id = HOT_ITEMS[selected_hotbar]
	_render_equipped()

func _render_equipped() -> void:
	for i in HOT_ITEMS.size():
		ui.toggle_element_class("#hot-%d" % i,"selected",i == selected_hotbar)
	ui.set_element_text("#equipped-name",str(_item(equipped_id).name).to_upper())
	ui.toggle_element_class("#weapon-info","hidden",equipped_id != "revolver")

func select_recipe(id: String) -> void:
	selected_recipe = clampi(id.trim_prefix("recipe-").to_int(),0,RECIPES.size()-1)
	_render_recipe()

func scavenge(_id: String = "") -> void:
	if camp_searched:
		return
	camp_searched = true
	_item("wood").count += 4
	_item("stone").count += 2
	_item("cloth").count += 2
	wood_gathered += 4
	_render_all()
	notify("FOUND  +4 dry wood   +2 river stone   +2 cloth scraps")

func use_selected(_id: String = "") -> void:
	_use(items[selected_item].id)

func _use(id: String) -> void:
	var item := _item(id)
	if int(item.count) <= 0:
		notify("Nothing left. Check your crafting recipes.")
		return
	match id:
		"canteen":
			if thirst >= 100:
				notify("You have had enough water for now.")
				return
			thirst = mini(100,thirst+25)
		"beans":
			if hunger >= 100:
				notify("Save your provisions. You are well fed.")
				return
			hunger = mini(100,hunger+25)
		"bandage":
			if health >= 100:
				notify("No wounds to dress.")
				return
			health = mini(100,health+20)
		"campfire":
			if camp_built:
				notify("Your camp is already established.")
				return
			camp_built = true
		"revolver", "hatchet":
			select_hotbar("hot-%d" % HOT_ITEMS.find(id))
			notify("EQUIPPED  " + str(item.name))
			return
		"knife":
			equipped_id = "knife"
			selected_hotbar = -1
			_render_equipped()
			notify("EQUIPPED  Trail knife")
			return
		_:
			show_crafting()
			return
	item.count -= 1
	_render_all()
	notify("CAMP ESTABLISHED  A light to come home to." if id == "campfire" else "USED  " + str(item.name))

func craft(_id: String = "") -> void:
	var recipe: Dictionary = RECIPES[selected_recipe]
	for id: String in recipe.cost:
		if int(_item(id).count) < int(recipe.cost[id]):
			notify("Not enough materials.")
			return
	for id: String in recipe.cost:
		_item(id).count -= int(recipe.cost[id])
	_item(recipe.result).count += 1
	_render_all()
	notify("CRAFTED  " + str(recipe.name) + " added to your satchel.")

func reload_weapon() -> void:
	if equipped_id != "revolver":
		return
	var rounds := mini(6-loaded,reserve)
	loaded += rounds
	reserve -= rounds
	_item("ammo").count = reserve
	_render_all()
	notify(("Cylinder full." if loaded == 6 else "No spare cartridges.") if rounds == 0 else "RELOADED  %d cartridges" % rounds)

func _input(event: InputEvent) -> void:
	if not is_visible_in_tree():
		return
	if event is InputEventKey and event.keycode == KEY_SHIFT:
		sprinting = event.pressed
	if not event is InputEventKey or not event.pressed or event.echo:
		return
	match event.keycode:
		KEY_TAB:
			if inventory_open: close_inventory()
			else: open_inventory()
		KEY_ESCAPE:
			if not inventory_open: return
			close_inventory()
		KEY_C: show_crafting()
		KEY_E:
			if not inventory_open: scavenge()
		KEY_F:
			if inventory_open:
				if crafting_open: craft()
				else: use_selected()
			else: _use(equipped_id)
		KEY_R: reload_weapon()
		KEY_SPACE:
			if inventory_open or equipped_id != "revolver": return
			if loaded > 0:
				loaded -= 1
				ui.set_element_text("#loaded","%02d" % loaded)
			else: notify("Empty cylinder. Press R to reload.")
		KEY_1, KEY_2, KEY_3, KEY_4, KEY_5, KEY_6:
			select_hotbar("hot-%d" % (event.keycode-KEY_1))
		_: return
	get_viewport().set_input_as_handled()

func _notification(what: int) -> void:
	if what == NOTIFICATION_WM_WINDOW_FOCUS_OUT:
		sprinting = false

func _process(delta: float) -> void:
	if ui == null:
		return
	if _toast_time > 0:
		_toast_time -= delta
		if _toast_time <= 0:
			ui.add_element_class("#toast","hidden")
	if not inventory_open:
		stamina = clampf(stamina + (-18.0 if sprinting else 12.0) * delta,0,100)
	_tick += delta
	if _tick >= 0.1:
		_tick = 0.0
		_render_stamina()
