extends Node2D

# `@font-face { font-family; src: url() }` in the stylesheet loads the font and
# registers the family, so `font-family: "Camp Mono"` selects it without any
# register_font_family call (CSS Fonts 4 §4; the host loads, the core lists).
# Every expectation is an equality against the same font registered natively,
# so the checks hold whatever the fixture fonts' metrics are.

var checks := 0
var failures := 0
const HTML := '<span id="a">Wide Words Here 0123</span><br><span id="b">Wide Words Here 0123</span>'
const SIZES := "#a, #b { font-size: 20px; } #b { font-family: sans-serif; }"

func _check(condition: bool, description: String) -> void:
	checks += 1
	if not condition:
		failures += 1
		printerr("FAIL  ", description)

func make_doc(css: String, base := "", registered: Font = null) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.size = Vector2(600, 200)
	add_child(doc)
	if not base.is_empty():
		doc.base_path = base
	if registered != null:
		doc.register_font_family("Camp Mono", registered)
	doc.css = css
	doc.html = HTML
	doc.update_document()
	return doc

func width(doc: WevaDocument, id: String) -> float:
	return doc.query_bounds(id).size.x

func _ready() -> void:
	var mono := FontFile.new()
	_check(mono.load_dynamic_font("res://fonts/WevaMonoMonospace.ttf") == OK, "fixture font loads natively")
	var sans := FontFile.new()
	_check(sans.load_dynamic_font("res://fonts/WevaMonoSans.ttf") == OK, "second fixture font loads natively")

	# Reference widths: the family registered by the game itself.
	var native := make_doc(SIZES + ' #a { font-family: "Camp Mono"; }', "", mono)
	var mono_width := width(native, "#a")
	var theme_width := width(native, "#b")
	_check(mono_width != theme_width, "fixture font measures differently from the theme font")
	var native_sans := make_doc(SIZES + ' #a { font-family: "Camp Mono"; }', "", sans)
	var sans_width := width(native_sans, "#a")

	var css := SIZES + ' @font-face { font-family: "Camp Mono"; src: url("fonts/WevaMonoMonospace.ttf") format("truetype"); } #a { font-family: "Camp Mono"; }'
	var doc := make_doc(css)
	_check(width(doc, "#a") == mono_width, "@font-face url() loads the font and the family selects it")
	_check(doc.get_css_diagnostics().is_empty(), "@font-face is no longer reported as unsupported")

	var relative := make_doc(SIZES + ' @font-face { font-family: "Camp Mono"; src: url(WevaMonoMonospace.ttf); } #a { font-family: "Camp Mono"; }', "res://fonts")
	_check(width(relative, "#a") == mono_width, "src resolves against base_path like an image url()")

	var missing := make_doc(SIZES + ' @font-face { font-family: "Camp Mono"; src: url("fonts/missing.ttf"); } #a { font-family: "Camp Mono"; }')
	_check(width(missing, "#a") == theme_width, "a missing source warns and falls back instead of failing")

	var precedence := make_doc(css, "", sans)
	_check(width(precedence, "#a") == sans_width, "the game's own register_font_family wins over @font-face")

	var faces := make_doc(SIZES + ' @font-face { font-family: "Camp Mono"; src: url(fonts/WevaMonoSans.ttf); font-weight: 700; } @font-face { font-family: "Camp Mono"; src: url(fonts/WevaMonoMonospace.ttf); font-weight: normal; } #a { font-family: "Camp Mono"; }')
	_check(width(faces, "#a") == mono_width, "with several faces the normal-weight one is registered")

	# A bold or italic rule is a real file for that weight or slant, used
	# instead of synthesis (synthesis keeps the regular advances, so a
	# different file is visible in the width). The nearest file serves what
	# no rule covers, and one axis synthesizes on top of the other's file.
	var weighted := SIZES + ' @font-face { font-family: "Camp Mono"; src: url(fonts/WevaMonoMonospace.ttf); } @font-face { font-family: "Camp Mono"; src: url(fonts/WevaMonoSans.ttf); font-weight: 700; } #a { font-family: "Camp Mono"; font-weight: 700; }'
	var bold := make_doc(weighted)
	_check(width(bold, "#a") == sans_width, "a bold @font-face file draws bold text instead of synthesis")
	bold.css = weighted.replace("font-weight: 700; }", "font-weight: 400; }")
	bold.update_document()
	_check(width(bold, "#a") == mono_width, "regular text keeps the regular file")
	bold.css = weighted.replace("#a { font-family: \"Camp Mono\"; font-weight: 700; }", "#a { font-family: \"Camp Mono\"; font-weight: 900; }")
	bold.update_document()
	_check(width(bold, "#a") == sans_width, "the nearest heavier file serves a weight no rule covers")
	bold.css = weighted.replace("#a { font-family: \"Camp Mono\"; font-weight: 700; }", "#a { font-family: \"Camp Mono\"; font-weight: 700; font-style: italic; }")
	bold.update_document()
	_check(width(bold, "#a") == sans_width, "bold italic slants the bold file when no bold-italic file exists")
	bold.css = weighted.replace("font-weight: 700; } #a", "font-style: italic; } #a").replace("#a { font-family: \"Camp Mono\"; font-weight: 700; }", "#a { font-family: \"Camp Mono\"; font-style: italic; }")
	bold.update_document()
	_check(width(bold, "#a") == sans_width, "an italic @font-face file draws italic text")
	bold.css = SIZES + ' @font-face { font-family: "Camp Mono"; src: url(fonts/WevaMonoMonospace.ttf); } #a { font-family: "Camp Mono"; font-weight: 700; }'
	bold.update_document()
	_check(width(bold, "#a") == mono_width, "dropping the bold rule returns bold text to synthesis")

	var native_bold := make_doc(SIZES + ' #a { font-family: "Camp Mono"; font-weight: 700; }', "", mono)
	native_bold.register_font_face("Camp Mono", sans, 700, false)
	native_bold.update_document()
	_check(width(native_bold, "#a") == sans_width, "register_font_face supplies a bold file to a native family")
	native_bold.register_font_face("Camp Mono", null, 700, false)
	native_bold.update_document()
	_check(width(native_bold, "#a") == mono_width, "and a null font removes it")

	# Replacing the stylesheet without the rule releases the family.
	doc.css = SIZES + ' #a { font-family: "Camp Mono"; }'
	doc.update_document()
	_check(width(doc, "#a") == theme_width, "removing @font-face on CSS replacement releases the family")
	doc.css = css
	doc.update_document()
	_check(width(doc, "#a") == mono_width, "and restoring it registers the family again")

	print("godot font face: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures > 0 else 0)
