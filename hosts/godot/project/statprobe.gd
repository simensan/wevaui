extends Control

# Headless check on the numbers the gallery's stats window shows, and a record
# of what they turned out to say.
#
# Run it with:
#   godot --headless --path . statprobe.tscn
#   WEVA_STAGE_LOG=1 godot --headless --path . statprobe.tscn   # the breakdown
#
# NOT in check.sh. It asserts on wall-clock times, which belong in a diagnostic
# rather than a gate -- the useful part is the numbers it prints, not the pass.
#
# What it found, on stats.html at 1280x720:
#
#   build, engine font    284 ms      of which layout is 241
#   build, stub font       49 ms
#   settled update          0 ms      the core's early-out, working
#
# The layout benchmarks in Tools/layoutbench.sh put this page at about 0.9 ms.
# They measure the STUB face. With the face the host actually uses, measuring
# text is the overwhelming majority of layout -- so those benchmarks are
# measuring a small fraction of what a real page costs, and the window shows
# both because F toggles between them.
#
# A full BUILD -- new document, parse, cascade, layout, paint -- must cost real
# time. weva_document_update on a settled document must not, because it early
# -outs, and timing it was the first version of this window's central lie.
const SAMPLES := "../../../Tools/oracle/corpus/samples"


func _ready() -> void:
    var dir := ProjectSettings.globalize_path("res://").path_join(SAMPLES).simplify_path()
    var fails := 0
    var checks := 0

    var t0 := Time.get_ticks_usec()
    var doc := WevaDocument.new()
    doc.document_size = Vector2(1280, 720)
    doc.css = FileAccess.open(dir.path_join("stats.css"), FileAccess.READ).get_as_text()
    doc.html = FileAccess.open(dir.path_join("stats.html"), FileAccess.READ).get_as_text()
    add_child(doc)
    doc.update_document()
    var build_ms := float(Time.get_ticks_usec() - t0) / 1000.0

    print("stats probe: build %.3f ms, %d draws, %d triangles" % [
        build_ms, doc.get_draw_count(), doc.get_triangle_count()])

    checks += 1
    if build_ms <= 0.05:
        print("FAIL a full build of stats.html reported %.4f ms" % build_ms)
        fails += 1

    checks += 1
    if doc.get_draw_count() <= 0:
        print("FAIL the page produced no draws")
        fails += 1

    # Settled: the core early-outs, so this must be small. Not asserted to be
    # exactly zero -- the caret and tooltip clocks still tick -- but it must be
    # far below the build, or the early-out has stopped working.
    doc.update_document(0.016)
    var idle := doc.get_last_update_ms()
    checks += 1
    if idle > build_ms * 0.5:
        print("FAIL a settled update cost %.3f ms against a %.3f ms build" % [idle, build_ms])
        fails += 1
    print("stats probe: settled update %.3f ms" % idle)

    # The FIRST build of the process pays for the engine font and its atlas,
    # which no later one does. If that were most of the number, the window
    # would be showing a one-off as though it were this page's cost.
    var t1 := Time.get_ticks_usec()
    var again := WevaDocument.new()
    again.document_size = Vector2(1280, 720)
    again.css = FileAccess.open(dir.path_join("stats.css"), FileAccess.READ).get_as_text()
    again.html = FileAccess.open(dir.path_join("stats.html"), FileAccess.READ).get_as_text()
    add_child(again)
    again.update_document()
    var second_ms := float(Time.get_ticks_usec() - t1) / 1000.0
    print("stats probe: second build %.3f ms (first was %.3f)" % [second_ms, build_ms])

    checks += 1
    if second_ms <= 0.05:
        print("FAIL a second build reported %.4f ms" % second_ms)
        fails += 1

    # The same page with the STUB face, which is what the layout benchmarks
    # measure. If the two are far apart, those benchmarks are not measuring
    # what the host actually pays for.
    var t2 := Time.get_ticks_usec()
    var stub := WevaDocument.new()
    stub.use_engine_font = false
    stub.document_size = Vector2(1280, 720)
    stub.css = FileAccess.open(dir.path_join("stats.css"), FileAccess.READ).get_as_text()
    stub.html = FileAccess.open(dir.path_join("stats.html"), FileAccess.READ).get_as_text()
    add_child(stub)
    stub.update_document()
    var stub_ms := float(Time.get_ticks_usec() - t2) / 1000.0
    print("stats probe: stub-font build %.3f ms against %.3f with the engine font"
        % [stub_ms, second_ms])

    # A spread across the corpus, because one page is thin evidence. Each is
    # built twice and the SECOND is reported: the first in the process pays for
    # the engine font and its atlas, which no later one does.
    print("")
    print("%-18s %10s %10s" % ["sample", "engine ms", "stub ms"])
    for name in ["stats", "vendor", "layout-stress", "quests", "randhtml",
                 "inventory", "glass", "menu", "todo", "card-component"]:
        var html_path := dir.path_join(name + ".html")
        if not FileAccess.file_exists(html_path):
            continue
        var css_path := dir.path_join(name + ".css")
        var html := FileAccess.open(html_path, FileAccess.READ).get_as_text()
        var css := "" if not FileAccess.file_exists(css_path) else             FileAccess.open(css_path, FileAccess.READ).get_as_text()
        var ms := [0.0, 0.0]
        for mode in 2:
            for pass_i in 2:
                var t := Time.get_ticks_usec()
                var d := WevaDocument.new()
                d.use_engine_font = mode == 0
                d.document_size = Vector2(1280, 720)
                d.css = css
                d.html = html
                add_child(d)
                d.update_document()
                ms[mode] = float(Time.get_ticks_usec() - t) / 1000.0
                d.queue_free()
        print("%-18s %10.1f %10.1f" % [name, ms[0], ms[1]])

    print("godot stats: %d checks, %d failures" % [checks, fails])
    get_tree().quit(1 if fails else 0)
