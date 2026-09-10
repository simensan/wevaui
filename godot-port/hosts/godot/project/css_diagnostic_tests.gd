extends Node

var checks := 0
var failures := 0

func require(ok: bool, label: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL CSS diagnostic: ", label)

func _ready() -> void:
	var doc := WevaDocument.new()
	doc.paused = true
	doc.document_size = Vector2(500, 400)
	doc.html = '<div id="label">Camp</div>'
	add_child(doc)
	require(doc.get_css_diagnostics().is_empty(), "empty initially")
	# @font-face is loaded by the host (font_face_tests), so @page stands in
	# for an unsupported rule here.
	doc.css = '@page{margin:0} @page :first{margin:1cm} @media(min-width:1000px){@future{div{color:red}}} @keyframes fade{from{opacity:0}to{opacity:1}}'
	var expected := PackedStringArray(["Ignored @page: unsupported stylesheet rule."])
	require(doc.get_css_diagnostics() == expected, "deduplicated; inactive media and keyframes omitted")
	var updates: int = doc.get_core_update_count()
	for index in 20:
		require(doc.get_css_diagnostics() == expected, "stable query %d" % index)
	require(doc.get_core_update_count() == updates, "query causes no document update")
	doc.document_size = Vector2(1200, 720)
	expected.append("Ignored @future: unsupported stylesheet rule.")
	require(doc.get_css_diagnostics() == expected, "viewport activates diagnostic")
	doc.css = 'div{color:green}'
	require(doc.get_css_diagnostics().is_empty(), "replacement clears obsolete diagnostics")
	doc.update_document(0)
	require(doc.get_computed_style("#label", "color") == "green", "supported styles still apply")
	doc.free()
	print("CSS diagnostics: ", checks, " checks, ", failures, " failures")
	get_tree().quit(1 if failures else 0)
