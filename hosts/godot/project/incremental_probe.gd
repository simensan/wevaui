extends SceneTree

# Real engine font and host bridge. Run with --headless --path . --script
# incremental_probe.gd. WEVA_PROBE_FRAMES=1 plus WEVA_PAINT_LOG=1 attributes
# the cold paint; the default measures 100 warm updates of each kind.
func _initialize() -> void:
    call_deferred("probe")

func probe() -> void:
    var frames := 100
    if OS.has_environment("WEVA_PROBE_FRAMES"):
        frames = maxi(1, int(OS.get_environment("WEVA_PROBE_FRAMES")))
    var dir := ProjectSettings.globalize_path("res://../../../Tools/oracle/corpus/samples")
    for page in ["hud", "layout-stress", "stats"]:
        var doc := WevaDocument.new()
        doc.document_size = Vector2(1280, 720)
        doc.css = FileAccess.get_file_as_string(dir.path_join(page + ".css"))
        doc.html = FileAccess.get_file_as_string(dir.path_join(page + ".html"))
        var started := Time.get_ticks_usec()
        root.add_child(doc)
        doc.update_document(0)
        print("%s cold %.3f ms" % [page, (Time.get_ticks_usec() - started) / 1000.0])
        var target := ".cell:last-child .mini-fill" if page == "layout-stress" else ".skill:last-child .skill-desc"
        if page == "layout-stress" or page == "stats":
            for property in ["padding-left", "background-color"]:
                var elapsed := 0
                for i in range(frames):
                    var value := ("11px" if i % 2 else "12px") if property == "padding-left" else ("#123456" if i % 2 else "#123457")
                    started = Time.get_ticks_usec()
                    if not doc.set_element_style(target, property, value):
                        push_error("probe target missing")
                        quit(1)
                        return
                    doc.update_document(0)
                    elapsed += Time.get_ticks_usec() - started
                print("%s %s %.3f ms/update" % [page, property, elapsed / float(frames) / 1000.0])
        var animated := 0
        for i in range(frames):
            started = Time.get_ticks_usec()
            doc.update_document(1.0 / 60.0)
            animated += Time.get_ticks_usec() - started
        print("%s animated %.3f ms/update" % [page, animated / float(frames) / 1000.0])
        doc.free()
    quit()
