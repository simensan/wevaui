extends Node2D

# A real screen, built the way someone would actually build one.
#
# The point is not the screen. Every binding in this file was added because
# something needed it, one at a time, and that finds gaps but never says when
# the surface is FINISHED. So this is the other direction: write an inventory
# the way a game would -- a gamepad-navigable grid, live data, a detail pane
# that follows the selection, an equip action, a filter -- and record every
# place the API made it awkward.
#
# Run it:  godot --path . inventory_demo.tscn
# Headless it asserts instead of drawing, so check.sh can run it.

var _doc: WevaDocument
var _headless := false
var _failures := 0
var _checks := 0

var _data := {
	"Filter": "all",
	"Selected": "",
	"Gold": 1240,
	"Items": [],
}

const HTML := """
<div class="screen">
  <header class="bar">
    <span class="title">Inventory</span>
    <span class="gold">{{ Gold }}g</span>
    <nav class="filters">
      <button id="f-all"    class="chip" data-class-on="FilterAll"    on-click="Filter">All</button>
      <button id="f-weapon" class="chip" data-class-on="FilterWeapon" on-click="Filter">Weapons</button>
      <button id="f-armour" class="chip" data-class-on="FilterArmour" on-click="Filter">Armour</button>
    </nav>
  </header>

  <div class="body">
    <ul class="grid" id="grid">
      <template data-each="Items as item" data-key="Id">
        <li class="slot" data-class-equipped="item.Equipped" data-class-dim="item.Hidden">
          <button class="cell" on-click="Select">
            <span class="glyph">{{ item.Glyph }}</span>
            <span class="name">{{ item.Name }}</span>
            <span class="count">{{ item.Count }}</span>
          </button>
        </li>
      </template>
    </ul>

    <aside class="detail">
      <h2 class="d-name">{{ Detail.Name }}</h2>
      <p class="d-kind">{{ Detail.Kind }}</p>
      <p class="d-text">{{ Detail.Text }}</p>
      <div class="d-stat"><span>Value</span><span>{{ Detail.Value }}g</span></div>
      <button id="equip" class="equip" on-click="Equip">{{ Detail.Action }}</button>
    </aside>
  </div>
</div>
"""

const CSS := """
* { box-sizing: border-box; }
body { margin: 0; font-family: sans-serif; font-size: 14px; color: #e8e8ef;
       background: #14141c; }
.screen { display: flex; flex-direction: column; height: 100%; }

.bar { display: flex; align-items: center; gap: 16px; padding: 12px 16px;
       background: #1e1e2a; border-bottom: 1px solid #2c2c3c; }
.title { font-size: 18px; font-weight: 700; }
.gold { color: #ffd479; }
.filters { display: flex; gap: 8px; margin-left: auto; }
.chip { padding: 4px 12px; border: 1px solid #3a3a50; background: #232333;
        color: #b9b9cc; border-radius: 4px; }
.chip.on { background: #3d3d7a; color: #fff; border-color: #5a5ac0; }
.chip:focus { outline: 2px solid #8a8aff; }

.body { display: flex; gap: 16px; padding: 16px; }

.grid { display: grid; grid-template-columns: repeat(4, 96px); gap: 10px;
        list-style: none; margin: 0; padding: 0; }
.slot { }
.slot.dim { display: none; }
.cell { width: 96px; height: 92px; display: flex; flex-direction: column;
        align-items: center; justify-content: center; gap: 2px;
        background: #232333; border: 1px solid #33334a; border-radius: 6px;
        color: #d8d8e8; }
.cell:focus { border-color: #8a8aff; background: #2c2c46; outline: none; }
.slot.equipped .cell { border-color: #ffd479; }
.glyph { font-size: 22px; }
.name { font-size: 11px; }
.count { font-size: 10px; color: #9a9ab0; }

.detail { width: 220px; padding: 12px; background: #1b1b26;
          border: 1px solid #2c2c3c; border-radius: 6px; }
.d-name { margin: 0 0 4px; font-size: 16px; }
.d-kind { margin: 0 0 10px; color: #9a9ab0; font-size: 12px; }
.d-text { margin: 0 0 12px; font-size: 12px; line-height: 17px; }
.d-stat { display: flex; justify-content: space-between; font-size: 12px;
          margin-bottom: 12px; }
.equip { width: 100%; padding: 8px; background: #3d3d7a; color: #fff;
         border: 1px solid #5a5ac0; border-radius: 4px; }
.equip:focus { outline: 2px solid #8a8aff; }
"""

func _check(condition: bool, description: String) -> void:
	_checks += 1
	if not condition:
		_failures += 1
		printerr("FAIL  ", description)

func _ready() -> void:
	_headless = DisplayServer.get_name() == "headless"

	_data["Items"] = [
		{"Id": "sword", "Glyph": "/", "Name": "Ashen Blade", "Kind": "weapon",
		 "Count": 1, "Value": 320, "Equipped": true,
		 "Text": "Chipped, and warmer than it should be."},
		{"Id": "bow", "Glyph": ")", "Name": "Hunting Bow", "Kind": "weapon",
		 "Count": 1, "Value": 180, "Equipped": false,
		 "Text": "Yew, restrung twice."},
		{"Id": "helm", "Glyph": "^", "Name": "Iron Helm", "Kind": "armour",
		 "Count": 1, "Value": 140, "Equipped": false,
		 "Text": "Dented above the left ear."},
		{"Id": "vest", "Glyph": "#", "Name": "Padded Vest", "Kind": "armour",
		 "Count": 1, "Value": 95, "Equipped": true,
		 "Text": "Smells of the road."},
		{"Id": "tonic", "Glyph": "o", "Name": "Green Tonic", "Kind": "item",
		 "Count": 6, "Value": 20, "Equipped": false,
		 "Text": "Tastes like a pond looks."},
		{"Id": "rope", "Glyph": "~", "Name": "Silk Rope", "Kind": "item",
		 "Count": 2, "Value": 45, "Equipped": false,
		 "Text": "Forty feet, give or take."},
	]

	_doc = WevaDocument.new()
	_doc.document_size = Vector2(760, 420)
	_doc.css = CSS
	_doc.html = HTML
	_doc.controller = self
	add_child(_doc)

	_select("sword")
	_refresh()

	_doc.element_focused.connect(_on_focused)

	if _headless:
		_run_assertions()

# ---- the controller the markup names ------------------------------------

func Filter(id: String) -> void:
	_data["Filter"] = id.trim_prefix("f-")
	_refresh()

func Select(_id: String) -> void:
	# A repeated row has no id of its own, so which one was clicked comes from
	# row_activated -- wired below rather than guessed at here.
	pass

func Equip(_id: String) -> void:
	for item in _data["Items"]:
		if item["Id"] == _data["Selected"]:
			item["Equipped"] = not item["Equipped"]
	# Detail is DERIVED from the item, so it has to be rebuilt rather than
	# left as it was -- the binding layer interpolates what the dictionary
	# says, and nothing recomputes a value the script owns.
	_select(_data["Selected"])
	_refresh()

func _on_focused(id: String) -> void:
	# Focusing a cell should update the detail pane, which is what makes a
	# gamepad feel right: the description follows the stick, no click needed.
	if id.is_empty():
		return

# ---- state -> markup ----------------------------------------------------

func _select(item_id: String) -> void:
	_data["Selected"] = item_id
	for item in _data["Items"]:
		if item["Id"] != item_id:
			continue
		_data["Detail"] = {
			"Name": item["Name"], "Kind": item["Kind"], "Text": item["Text"],
			"Value": item["Value"],
			"Action": "Unequip" if item["Equipped"] else "Equip",
		}

func _refresh() -> void:
	var filter: String = _data["Filter"]
	for item in _data["Items"]:
		item["Hidden"] = filter != "all" and item["Kind"] != filter
	_data["FilterAll"] = filter == "all"
	_data["FilterWeapon"] = filter == "weapon"
	_data["FilterArmour"] = filter == "armour"
	_doc.data = _data
	_doc.update_document()

# ---- input --------------------------------------------------------------

func _unhandled_input(event: InputEvent) -> void:
	if not (event is InputEventKey) or not event.pressed or event.echo:
		return
	var moved := ""
	match event.keycode:
		KEY_LEFT:  moved = _doc.focus_move(Vector2.LEFT)
		KEY_RIGHT: moved = _doc.focus_move(Vector2.RIGHT)
		KEY_UP:    moved = _doc.focus_move(Vector2.UP)
		KEY_DOWN:  moved = _doc.focus_move(Vector2.DOWN)
		KEY_ENTER, KEY_SPACE:
			var focused := _doc.get_focused_id()
			if focused == "equip":
				Equip(focused)
		_:
			return
	get_viewport().set_input_as_handled()
	if not moved.is_empty():
		_follow_focus()

func _follow_focus() -> void:
	# Which ITEM is focused. The cell carries no id -- the template writes one
	# element and the data decides how many there are -- so the row's identity
	# has to come from the engine.
	var row: Dictionary = _doc.get_focused_row()
	if row.has("key") and row["key"] != _data["Selected"]:
		_select(row["key"])
		_refresh()

func _run_assertions() -> void:
	# The grid built itself from the data.
	_check(_doc.count_elements("#grid > .slot") == 6, "every item has a slot")
	_check(_doc.query_text(".gold").contains("1240"), "the header binds gold")
	_check(_doc.query_text(".d-name").contains("Ashen"), "the detail pane starts filled")

	# Filtering hides rows without rebuilding the list.
	Filter("f-weapon")
	_check(_doc.count_elements("#grid > .slot") == 6, "filtering keeps the rows")
	_check(_doc.get_computed_style("#grid > .slot:nth-of-type(3)", "display") == "none",
			"and hides the ones that do not match")
	_check(_doc.get_computed_style("#grid > .slot:nth-of-type(1)", "display") != "none",
			"while leaving the ones that do")
	Filter("f-all")

	# The gamepad walks the grid, and the detail pane follows.
	_doc.set_focus("#grid > .slot:nth-of-type(1) .cell")
	_follow_focus()
	_check(_data["Selected"] == "sword", "focusing a cell selects its item")
	_doc.focus_move(Vector2.RIGHT)
	_follow_focus()
	_check(_data["Selected"] == "bow", "and moving right selects the next")
	_check(_doc.query_text(".d-name").contains("Hunting"), "the detail pane follows")

	# Equipping writes back through the controller and the markup follows.
	# The bow is selected by now and is NOT equipped, so the action offers to
	# equip it; the sword, which starts equipped, would offer the opposite.
	_check(_doc.query_text("#equip") == "Equip", "the action offers to equip")
	Equip("equip")
	_check(_doc.query_text("#equip") == "Unequip", "and flips once it is")

	print("godot inventory: %d checks, %d failures" % [_checks, _failures])
	get_tree().quit(1 if _failures > 0 else 0)
