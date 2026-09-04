extends Control

# What a FRAME costs, as opposed to a build.
#
# The gallery's stats window reports frame time, and a static page should cost
# nothing: the draw list has not changed, so there is nothing to re-submit.
# This checks that claim, because the node used to queue a redraw on every
# update_document call whether or not anything had changed -- and a redraw
# rebuilds three PackedArrays per draw and converts every vertex to sRGB.
const SAMPLES := "../../../tools/oracle/corpus/samples"
const PAGE := "stats"
const FRAMES := 240

var _doc: WevaDocument = null
var _n := 0
var _t0 := 0
var _redraws := 0


func _ready() -> void:
    var dir := ProjectSettings.globalize_path("res://").path_join(SAMPLES).simplify_path()
    _doc = WevaDocument.new()
    _doc.document_size = Vector2(1280, 720)
    _doc.css = FileAccess.open(dir.path_join(PAGE + ".css"), FileAccess.READ).get_as_text()
    _doc.html = FileAccess.open(dir.path_join(PAGE + ".html"), FileAccess.READ).get_as_text()
    add_child(_doc)
    _doc.update_document()
    _doc.draw.connect(func() -> void: _redraws += 1)
    print("frame probe: %s, %d draws, %d triangles" % [
        PAGE, _doc.get_draw_count(), _doc.get_triangle_count()])
    _t0 = Time.get_ticks_usec()


var _phase := 0
var _update_us := 0


func _process(delta: float) -> void:
    # Phase 0 drives the document exactly as a game is told to; phase 1 does
    # not touch it at all. The DIFFERENCE is what this engine costs per frame.
    # Without the second phase the number is Godot's own frame floor plus ours,
    # reported as if it were all ours.
    if _phase == 0:
        var t := Time.get_ticks_usec()
        _doc.update_document(delta)
        _update_us += Time.get_ticks_usec() - t
    elif _phase == 2:
        # What an ANIMATING page pays: the draw list is republished, so every
        # draw is walked and every vertex converted again.
        _doc.queue_redraw()
    _n += 1
    if _n < FRAMES:
        return

    var total := float(Time.get_ticks_usec() - _t0) / 1000.0
    if _phase == 0:
        print("frame probe: driven   %6.3f ms/frame   update %6.3f ms   %d redraws" % [
            total / FRAMES, float(_update_us) / FRAMES / 1000.0, _redraws])
        _phase = 1
        _n = 0
        _redraws = 0
        _t0 = Time.get_ticks_usec()
        return
    if _phase == 1:
        print("frame probe: idle     %6.3f ms/frame   (Godot's own floor)   %d redraws" % [
            total / FRAMES, _redraws])
        _phase = 2
        _n = 0
        _redraws = 0
        _t0 = Time.get_ticks_usec()
        return
    print("frame probe: redrawn  %6.3f ms/frame   %d redraws   (a full re-submit each frame)" % [
        total / FRAMES, _redraws])
    get_tree().quit()
