extends Node

var checks := 0
var failures := 0
var viewport: SubViewport
const TEXT := "ABAB"

func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		printerr("FAIL  ", message)

# A real bitmap FontFile resource, built without platform fonts or imports.
# Two distinguishable glyph shapes let rendered checks detect a stale atlas.
func make_font(advance: int, wide: bool, letters := "AB") -> FontFile:
	var font := FontFile.new()
	font.fixed_size = 16
	font.set_cache_ascent(0, 16, 12)
	font.set_cache_descent(0, 16, 4)
	var pixels := Image.create(16, 16, false, Image.FORMAT_RGBA8)
	pixels.fill(Color.TRANSPARENT)
	pixels.fill_rect(Rect2i(0, 0, 7 if wide else 3, 9), Color.WHITE)
	font.set_texture_image(0, Vector2i(16, 0), 0, pixels)
	for letter in letters:
		var glyph: int = letter.unicode_at(0)
		font.set_glyph_advance(0, 16, glyph, Vector2(advance, 0))
		font.set_glyph_offset(0, Vector2i(16, 0), glyph, Vector2(0, -9))
		font.set_glyph_size(0, Vector2i(16, 0), glyph, Vector2(7 if wide else 3, 9))
		font.set_glyph_uv_rect(0, Vector2i(16, 0), glyph, Rect2(0, 0, 7 if wide else 3, 9))
		font.set_glyph_texture_idx(0, Vector2i(16, 0), glyph, 0)
	return font

func make_doc(parent: Control) -> WevaDocument:
	var doc := WevaDocument.new()
	doc.document_size = Vector2(320, 120)
	doc.paused = true
	doc.css = "html,body{margin:0}body{padding:20px;color:white}span{display:inline-block;font-size:16px;line-height:24px}"
	doc.html = '<span id="text">' + TEXT + '</span>'
	parent.add_child(doc)
	doc.update_document(0)
	return doc

func check_width(doc: WevaDocument, font: Font, message: String) -> void:
	# Shape afresh: Font.get_string_size rounds upward, and these fixtures also
	# exercise low-level bitmap-cache edits rather than cached line measurements.
	var server := TextServerManager.get_primary_interface()
	var shaped := server.create_shaped_text()
	server.shaped_text_add_string(shaped, TEXT, font.get_rids(), 16)
	# The shaped buffer's extent can round up as well. Layout consumes the
	# positioned glyph advances, including repeats, at their original precision.
	var expected := 0.0
	for glyph in server.shaped_text_get_glyphs(shaped):
		expected += float(glyph.advance) * int(glyph.repeat)
	server.free_rid(shaped)
	check(expected > 0, message + " has nonzero native metrics")
	var actual := doc.query_bounds("#text").size.x
	check(absf(actual - expected) < 0.01, message + " (actual %.2f, native %.2f, resource %s)" % [actual, expected, str(doc.get_theme_font("font") == font)])

func settle() -> void:
	await get_tree().process_frame
	await get_tree().process_frame

func snapshot() -> PackedByteArray:
	await settle()
	await RenderingServer.frame_post_draw
	return viewport.get_texture().get_image().get_data()

func check_default_font_ownership(parent: Control) -> void:
	var font := ThemeDB.get_fallback_font()
	# Array assignment aliases the source; take a real snapshot before any
	# document adopts the font so this assertion cannot change with the bug.
	var original: Array[RID] = font.get_rids().duplicate()
	check(not original.is_empty(), "Default font has native RIDs")
	var peer := make_doc(parent)
	check(peer.has_engine_font(), "Default font is active in the peer")
	check(font.get_rids() == original, "Adopting the default font preserves its RID array")
	for cycle in range(6):
		var doc := make_doc(parent)
		check(doc.has_engine_font(), "Repeated default font adoption is active")
		check(font.get_rids() == original, "Opening document %d preserves default font RIDs (%d -> %d)" % [cycle, original.size(), font.get_rids().size()])
		doc.use_engine_font = false
		doc.update_document(0)
		doc.use_engine_font = true
		doc.update_document(0)
		check(font.get_rids() == original, "Re-enabling the engine font preserves default font RIDs")
		check_width(peer, font, "Existing peer still uses the native default font")
		doc.free()
		check(font.get_rids() == original, "Destroying a document preserves default font RIDs")
	peer.free()
	check(font.get_rids() == original, "Default font RIDs remain unchanged after all documents are freed")

func _ready() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(320, 120)
	viewport.transparent_bg = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(viewport)
	var parent := Control.new()
	parent.size = Vector2(320, 120)
	viewport.add_child(parent)
	check_default_font_ownership(parent)
	var a := make_font(7, false)
	var b := make_font(13, true)
	var theme := Theme.new()
	theme.default_font = a
	parent.theme = theme
	var doc := make_doc(parent)
	await settle()
	check(doc.has_engine_font(), "Theme font is adopted")
	check_width(doc, a, "Inherited theme default font controls layout")
	var initial := PackedByteArray()
	if DisplayServer.get_name() != "headless":
		initial = await snapshot()
		check(initial.count(255) > 0, "Inherited bitmap font actually renders")

	doc.add_theme_font_override("font", b)
	await settle()
	check_width(doc, b, "Node font override replaces inherited font")
	if not initial.is_empty():
		check(await snapshot() != initial, "Override changes glyph pixels")
	doc.remove_theme_font_override("font")
	await settle()
	check_width(doc, a, "Removing override restores inherited font")
	if not initial.is_empty():
		check(await snapshot() == initial, "Restoring a font restores its pixels")

	theme.default_font = b
	await settle()
	check_width(doc, b, "Replacing the parent theme font reaches a paused document")
	# Same FontFile identity and glyph indices, different metrics and bitmap.
	for letter in "AB":
		b.set_glyph_advance(0, 16, letter.unicode_at(0), Vector2(17, 0))
	var changed := Image.create(16, 16, false, Image.FORMAT_RGBA8)
	changed.fill(Color.TRANSPARENT)
	changed.fill_rect(Rect2i(0, 0, 7, 4), Color.WHITE)
	b.set_texture_image(0, Vector2i(16, 0), 0, changed)
	# Low-level glyph-cache setters do not emit Resource.changed themselves.
	b.emit_changed()
	await settle()
	check_width(doc, b, "Mutating a FontFile refreshes measured runs")
	var live_pixels := PackedByteArray()
	if not initial.is_empty():
		live_pixels = await snapshot()
		doc.hide()
		var fresh := make_doc(parent)
		check(await snapshot() == live_pixels, "Mutated font paints exactly like a fresh document")
		fresh.free()
		doc.show()

	theme.set_type_variation("GameText", "WevaDocument")
	theme.set_font("font", "GameText", a)
	doc.theme_type_variation = "GameText"
	await settle()
	check_width(doc, a, "Theme type variation selects its font")
	doc.theme_type_variation = ""
	await settle()
	check_width(doc, b, "Clearing type variation restores theme default")
	var variation := FontVariation.new()
	variation.base_font = a
	variation.set_spacing(TextServer.SPACING_GLYPH, 3)
	doc.add_theme_font_override("font", variation)
	await settle()
	check_width(doc, variation, "FontVariation resources retain native spacing")
	variation.set_spacing(TextServer.SPACING_GLYPH, 6)
	await settle()
	check_width(doc, variation, "Normal resource setters refresh without manual notification")

	# Font fallback resources forward changes to their owning Font resource.
	var primary := make_font(5, false, "X")
	var fallback := make_font(11, true)
	primary.fallbacks = [fallback]
	doc.add_theme_font_override("font", primary)
	await settle()
	check_width(doc, primary, "Explicit font fallback supplies missing glyphs")
	fallback.set_glyph_advance(0, 16, 66, Vector2(19, 0))
	fallback.emit_changed()
	await settle()
	check_width(doc, primary, "Fallback resource mutation refreshes the document")

	# Refreshing or destroying one document must not free another's font RIDs.
	var peer := make_doc(parent)
	peer.add_theme_font_override("font", primary)
	peer.hide()
	await settle()
	check_width(peer, primary, "Two documents share the resource")
	doc.use_engine_font = false
	doc.update_document(0)
	check(not doc.has_engine_font(), "Disabling engine font restores the stub")
	check_width(peer, primary, "Disabling one backend keeps the peer valid")
	doc.use_engine_font = true
	doc.update_document(0)
	check_width(doc, primary, "Re-enabling engine font uses the current override")
	peer.free()
	fallback.set_glyph_advance(0, 16, 65, Vector2(8, 0))
	fallback.emit_changed()
	await settle()
	check_width(doc, primary, "Font changes remain valid after a peer is freed")
	doc.remove_theme_font_override("font")
	var other := Control.new()
	other.theme = Theme.new()
	other.theme.default_font = a
	viewport.add_child(other)
	doc.reparent(other)
	await settle()
	check_width(doc, a, "Reparenting resolves the new inherited font")
	doc.free()
	a.set_glyph_advance(0, 16, 65, Vector2(9, 0))
	a.emit_changed()
	await settle()
	check(true, "Changing a released document's former font remains safe")
	print("godot theme fonts: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures else 0)
