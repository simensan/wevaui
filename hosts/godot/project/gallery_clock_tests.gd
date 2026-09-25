extends Node

var elapsed := 0.0

func _process(delta: float) -> void:
	elapsed += delta

func _ready() -> void:
	var gallery = load("res://gallery.tscn").instantiate()
	add_child(gallery)
	var doc: WevaDocument = gallery.get("_doc")
	doc.css = 'html,body{margin:0}#clock{position:absolute;width:20px;height:20px;animation:move 1000s linear infinite}@keyframes move{from{left:0px}to{left:10000px}}'
	doc.html = '<div id="clock"></div>'
	doc.update_document(0)
	await get_tree().process_frame
	await get_tree().process_frame
	var before := doc.query_bounds("#clock").position.x
	var started := elapsed
	for i in 30:
		await get_tree().process_frame
	var actual := doc.query_bounds("#clock").position.x-before
	var expected := (elapsed-started)*10.0
	var ok := absf(actual-expected) < 0.01
	if not ok:
		printerr("FAIL gallery animation advanced %.5f pixels for %.5f seconds; expected %.5f" % [actual,elapsed-started,expected])
	print("godot gallery clock: 1 checks, %d failures" % (0 if ok else 1))
	get_tree().quit(0 if ok else 1)
