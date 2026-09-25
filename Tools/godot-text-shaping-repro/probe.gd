extends SceneTree

# Standalone TextServer calls: no addon, native input, or rendering required.
var keep_fonts: Array[SystemFont] = []
var fonts: Array[RID] = []
var checks := 0
var failures := 0

func _initialize() -> void:
	call_deferred("run_probe")

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL ", message)

func shape(text: String) -> Array[Dictionary]:
	var ts := TextServerManager.get_primary_interface()
	var buffer := ts.create_shaped_text()
	ts.shaped_text_add_string(buffer,text,fonts,16)
	ts.shaped_text_shape(buffer)
	var glyphs := ts.shaped_text_get_glyphs(buffer)
	ts.free_rid(buffer)
	return glyphs

func run_probe() -> void:
	print("Engine: ", Engine.get_version_info().string)
	var support_data := OS.get_environment("GODOT_TEXT_SUPPORT_DATA")
	if not support_data.is_empty() and not TextServerManager.get_primary_interface().load_support_data(support_data):
		printerr("FAIL Could not load TextServer support data")
		quit(2)
		return
	print("Text server: ",TextServerManager.get_primary_interface().get_name())
	# Runtime templates omit the editor's embedded ICU data. Without it,
	# script detection is bypassed and this regression can falsely pass.
	if TextServerManager.get_primary_interface().string_to_upper("i","tr") != "İ":
		printerr("FAIL ICU support data is required; set GODOT_TEXT_SUPPORT_DATA")
		quit(2)
		return
	fonts.append_array(ThemeDB.fallback_font.get_rids())
	for name in ["Segoe UI Symbol","Segoe UI Emoji","Noto Color Emoji","Noto Sans Symbols2","Noto Sans Symbols","DejaVu Sans","Symbola"]:
		if not OS.get_system_fonts().has(name):
			continue
		var font := SystemFont.new()
		font.font_names = [name]
		keep_fonts.append(font)
		fonts.append_array(font.get_rids())
	var args := OS.get_cmdline_user_args()
	var scenario := args[0] if not args.is_empty() else "emoji33"
	var pieces: Array[String] = []
	var repetitions: Array[int] = []
	match scenario:
		"emoji32": pieces = ["á😀b"]; repetitions = [32]
		"emoji33": pieces = ["á😀b"]; repetitions = [33]
		"emoji65": pieces = ["á😀b"]; repetitions = [65]
		"emoji256": pieces = ["á😀b"]; repetitions = [256]
		"multiple_scripts": pieces = ["á😀b","Ж"]; repetitions = [33,1]
		"multiple_growing_scripts": pieces = ["á😀b","Ж😀б","á😀b"]; repetitions = [33,65,33]
		_:
			printerr("Unknown scenario: ",scenario)
			quit(2)
			return
	var text := ""
	var expected: Array[Vector2i] = []
	var expected_width := 0.0
	for i in pieces.size():
		var unit := pieces[i]
		var unit_glyphs := shape(unit)
		for j in repetitions[i]:
			for glyph in unit_glyphs:
				expected.append(Vector2i(glyph.start+text.length(),glyph.end+text.length()))
				expected_width += float(glyph.advance)*int(glyph.repeat)
			text += unit
	var actual := shape(text)
	var actual_ranges: Array[Vector2i] = []
	var actual_width := 0.0
	for glyph in actual:
		actual_ranges.append(Vector2i(glyph.start,glyph.end))
		actual_width += float(glyph.advance)*int(glyph.repeat)
	check(actual.size() == expected.size(),"Glyph count %d, expected %d" % [actual.size(),expected.size()])
	check(actual_ranges == expected,"Source clusters cover each character once in order")
	check(absf(actual_width-expected_width) < 0.01,"Advance %.4f, expected %.4f" % [actual_width,expected_width])
	print("%s: %d glyphs, %.4f advance; %d checks, %d failures" % [scenario,actual.size(),actual_width,checks,failures])
	quit(1 if failures else 0)
