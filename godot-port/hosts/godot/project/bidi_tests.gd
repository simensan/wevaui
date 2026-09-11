extends Node2D

# CSS Writing Modes 3 §2 through a real shaper: the core splits a paragraph's
# runs at bidi level changes and places them in visual order (UAX #9 L2);
# TextServer shapes each Hebrew run right-to-left on its own. Span bounds are
# what a page's layout sees, so they are what is checked.

var failures := 0
var checks := 0

func _check(condition: bool, description: String) -> void:
	checks += 1
	if not condition:
		failures += 1
		printerr("FAIL  ", description)

const CSS := """
html, body { margin: 0; width: 400px; font-size: 16px }
#r, #r2 { direction: rtl }
#o { unicode-bidi: bidi-override; direction: rtl }
#iso { unicode-bidi: isolate; direction: rtl }
"""

const HTML := """
<div id=l><span id=la>abc</span> <span id=lb>ABG</span> <span id=lc>def</span></div>
<div id=r><span id=ra>abc</span> <span id=rb>ABG</span></div>
<div id=r2><span id=r2a>ABG</span> <span id=r2b>DHV</span></div>
<div id=p><span id=o><span id=oa>abc</span> <span id=ob>def</span></span> <span id=oc>ghi</span></div>
<div id=q><span id=qa>one</span> <span id=iso><span id=qb>ABG</span> <span id=qc>DHV</span></span> <span id=qd>two</span></div>
<div id=plain><span id=pa>just</span> <span id=pb>latin</span></div>
"""

func _x(doc: WevaDocument, id: String) -> float:
	return doc.query_bounds(id).position.x

func _ready() -> void:
	var doc := WevaDocument.new()
	doc.document_size = Vector2(400, 300)
	doc.css = CSS
	doc.html = HTML.replace("ABG", "אבג").replace("DHV", "דהו")
	add_child(doc)
	doc.update_document()
	await get_tree().process_frame

	_check(doc.query_bounds("#lb").size.x > 0, "the Hebrew span has a width (the theme font or a fallback covers it)")
	_check(_x(doc, "#la") < _x(doc, "#lb") and _x(doc, "#lb") < _x(doc, "#lc"),
		"a left-to-right paragraph keeps its logical order")
	_check(_x(doc, "#rb") < _x(doc, "#ra"),
		"a right-to-left paragraph draws the Latin word, logically first, right of the Hebrew")
	var ra := doc.query_bounds("#ra")
	_check(absf(ra.position.x + ra.size.x - 400.0) < 0.5, "and the line hugs the right edge")
	_check(_x(doc, "#r2b") < _x(doc, "#r2a"), "two Hebrew words: the logically first is the rightmost")
	_check(_x(doc, "#ob") < _x(doc, "#oa") and _x(doc, "#oa") < _x(doc, "#oc"),
		"bidi-override on a right-to-left span reverses its words, the text after it stays put")
	_check(_x(doc, "#qa") < _x(doc, "#qc") and _x(doc, "#qc") < _x(doc, "#qb") and _x(doc, "#qb") < _x(doc, "#qd"),
		"an isolate reverses inside and leaves its surroundings in place")
	_check(_x(doc, "#pa") < _x(doc, "#pb") and _x(doc, "#pa") == 0.0, "plain Latin is untouched")

	doc.queue_free()
	print("godot bidi: %d checks, %d failures" % [checks, failures])
	get_tree().quit(1 if failures > 0 else 0)
