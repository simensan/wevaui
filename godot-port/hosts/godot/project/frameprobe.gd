extends Control

# What an ANIMATED page costs per frame, and what a hover costs on top.
#
# Frame time is not the instrument: headless Godot has a floor near 6.9ms that
# swamps it. update_document is timed directly instead, which is the engine's
# own work and is not affected by the floor.
# Read it with the stage and paint logs for a breakdown:
#   WEVA_STAGE_LOG=1 godot --headless --path . frameprobe.tscn
#   WEVA_PAINT_LOG=1 godot --headless --path . frameprobe.tscn
#
# That pair is what found the text-shaping cost: the stage log said paint, the
# paint log said text, and the ABI adapter said it was being asked twice a
# frame for something that had not changed.
const SAMPLES := "../../../tools/oracle/corpus/samples"
const FRAMES := 200

var _doc: WevaDocument = null
# Every sample with an `infinite` keyframe animation, worst first by how many,
# plus two static ones as a control -- an animated page invalidates every frame
# and a static one does not, and the difference is the whole point.
var _pages: Array[String] = [
    "neon", "particles", "match3", "match3-endgame", "hud", "glass",
    "layout-stress", "combat-hud", "audit-validation", "story-bubble",
    "stats", "vendor",
]
var _page := 0
var _n := 0
var _update_us := 0
var _worst_us := 0
var _redraws := 0
var _hovering := false
var _hover_us := 0
var _hover_n := 0
var _hover_worst := 0


func _load(name: String) -> void:
    var dir := ProjectSettings.globalize_path("res://").path_join(SAMPLES).simplify_path()
    if _doc != null:
        _doc.queue_free()
    _doc = WevaDocument.new()
    _doc.document_size = Vector2(1280, 720)
    _doc.interactive = true
    var css_path := dir.path_join(name + ".css")
    _doc.css = "" if not FileAccess.file_exists(css_path) else \
        FileAccess.open(css_path, FileAccess.READ).get_as_text()
    _doc.html = FileAccess.open(dir.path_join(name + ".html"), FileAccess.READ).get_as_text()
    add_child(_doc)
    _doc.update_document()
    _doc.draw.connect(func() -> void: _redraws += 1)
    _n = 0
    _update_us = 0
    _worst_us = 0
    _redraws = 0
    _hover_us = 0
    _hover_n = 0
    _hover_worst = 0


func _ready() -> void:
    print("%-14s %10s %10s %8s %12s %10s" % [
        "page", "update ms", "worst ms", "redraws", "hover ms", "hover worst"])
    _load(_pages[0])


func _process(delta: float) -> void:
    # Half the run idle, half of it moving the pointer -- a hover is a restyle
    # and whatever that invalidates, which is the thing that spikes.
    var hover := _n >= FRAMES / 2
    if hover:
        # A different point each frame, so :hover actually changes.
        _doc.set_pointer(Vector2(200 + (_n % 40) * 20, 120 + (_n % 17) * 30), 0)
    var t := Time.get_ticks_usec()
    _doc.update_document(delta)
    var us := Time.get_ticks_usec() - t
    if hover:
        _hover_us += us
        _hover_n += 1
        _hover_worst = maxi(_hover_worst, us)
    else:
        _update_us += us
        _worst_us = maxi(_worst_us, us)
    _n += 1
    if _n < FRAMES:
        return

    var idle_n := FRAMES / 2
    print("%-14s %10.3f %10.3f %8d %12.3f %10.3f" % [
        _pages[_page], float(_update_us) / idle_n / 1000.0, float(_worst_us) / 1000.0,
        _redraws, float(_hover_us) / maxi(1, _hover_n) / 1000.0,
        float(_hover_worst) / 1000.0])
    _page += 1
    if _page >= _pages.size():
        get_tree().quit()
        return
    _load(_pages[_page])
