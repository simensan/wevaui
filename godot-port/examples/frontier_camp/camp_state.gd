extends RefCounted
## Game rules and writable state. No DOM, selectors, or Weva dependency.
signal changed

var model := {
	"Player": {"Name": "Morgan", "Health": 72, "Stamina": 86, "Gold": 18},
	"Settings": {"Volume": 65, "Music": true},
	"Items": [
		{"Id": "beans", "Name": "Baked beans", "Detail": "Restores 20 stamina", "Count": 2, "Action": "Eat"},
		{"Id": "bandage", "Name": "Clean bandage", "Detail": "Restores 20 health", "Count": 2, "Action": "Use"},
		{"Id": "wood", "Name": "Dry wood", "Detail": "Crafting material", "Count": 3, "Action": "Material"},
		{"Id": "stone", "Name": "River stone", "Detail": "Crafting material", "Count": 2, "Action": "Material"}
	],
	"View": {"Message": "A little warmth against a very big night.", "Objective": "Build a fire before sundown.", "Clock": "17:40", "CampBuilt": false}
}
var elapsed_seconds := 0

func _init() -> void:
	derive()

func item(key: String) -> Dictionary:
	for entry in model.Items:
		if entry.Id == key:
			return entry
	return {}

func count(key: String) -> int:
	return int(item(key).get("Count", 0))

func _add(key: String, amount: int) -> void:
	var entry := item(key)
	if entry.is_empty():
		if key == "campfire":
			entry = {"Id": key, "Name": "Campfire kit", "Detail": "Establish your camp", "Count": 0, "Action": "Place"}
		elif key == "wood":
			entry = {"Id": key, "Name": "Dry wood", "Detail": "Crafting material", "Count": 0, "Action": "Material"}
		else:
			return
		model.Items.append(entry)
	entry.Count += amount

func derive() -> void:
	model.View.CraftDisabled = count("wood") < 4 or count("stone") < 2 or model.View.CampBuilt or count("campfire") > 0
	model.View.Wood = count("wood")
	model.View.Stone = count("stone")
	model.View.Stacks = model.Items.size()
	for entry in model.Items:
		entry.Icon = "assets/" + entry.Id + ".svg"
		entry.Disabled = (entry.Id in ["wood", "stone"] or
			(entry.Id == "beans" and model.Player.Stamina >= 100) or
			(entry.Id == "bandage" and model.Player.Health >= 100))

func publish() -> void:
	derive()
	changed.emit()

func forage() -> void:
	_add("wood", 3)
	model.Player.Stamina = maxi(0, model.Player.Stamina - 8)
	model.Player.Gold += 1
	model.View.Message = "Found 3 dry wood and a silver dollar."
	publish()

func take_damage() -> void:
	model.Player.Health = maxi(0, model.Player.Health - 15)
	model.View.Message = "A thorny trail. Lost 15 health."
	publish()

func craft() -> void:
	if model.View.CraftDisabled:
		return
	item("wood").Count -= 4
	item("stone").Count -= 2
	_remove_empty_stacks()
	_add("campfire", 1)
	model.View.Message = "Campfire kit crafted. Place it from your satchel."
	publish()

func use_item(key: String) -> void:
	var entry := item(key)
	if entry.is_empty() or entry.Disabled:
		return
	match key:
		"beans": model.Player.Stamina = mini(100, model.Player.Stamina + 20)
		"bandage": model.Player.Health = mini(100, model.Player.Health + 20)
		"campfire":
			model.View.CampBuilt = true
			model.View.Objective = "Camp established. Rest easy, traveler."
		_: return
	entry.Count -= 1
	model.View.Message = "Used " + entry.Name.to_lower() + "."
	_remove_empty_stacks()
	publish()

func _remove_empty_stacks() -> void:
	for i in range(model.Items.size() - 1, -1, -1):
		if model.Items[i].Count <= 0:
			model.Items.remove_at(i)

func sort_items() -> void:
	model.Items.reverse()
	model.View.Message = "Satchel order reversed."
	publish()

func tick() -> void:
	elapsed_seconds += 1
	var minutes := 17 * 60 + 40 + elapsed_seconds
	model.View.Clock = "%02d:%02d" % [(minutes / 60) % 24, minutes % 60]
	# Native Timer is the game clock; bindings only refresh when state changes.
	if model.View.CampBuilt:
		model.Player.Stamina = mini(100, model.Player.Stamina + 1)
	publish()
