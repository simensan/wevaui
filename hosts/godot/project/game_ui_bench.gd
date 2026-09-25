extends SceneTree

# Representative runtime work through the public GDScript API. Cold creation
# is deliberately outside the timer. Set WEVA_GAME_BENCH_OUT to a directory
# for JSON, fixture HTML/CSS and deterministic final-frame captures.
const CSS := """
html,body{margin:0;font:16px sans-serif;color:#e8edf5}
*{box-sizing:border-box}
.panel{position:absolute;background:#172433;border:1px solid #405268;border-radius:8px;padding:16px}
h2{font-size:22px;margin:0 0 12px}p{margin:4px 0}
.vitals{left:24px;top:24px;width:300px}.track{height:18px;background:#090e18;margin:8px 0;border-radius:4px;overflow:hidden}
.fill{height:18px;width:80%;background:#55c989}.mana{background:#559fef}.stamina{background:#ecc462}
.objectives{right:24px;top:24px;width:300px}.party{left:24px;top:230px;width:240px}
.member{height:40px;margin-bottom:8px}.member .track{height:6px;margin:4px 0}.member .fill{height:6px}
.actions{left:350px;bottom:24px;display:flex;gap:8px}.slot{width:64px;height:64px;padding:8px;background:#24354c;border:1px solid #506681;border-radius:6px}
.ammo{right:24px;bottom:24px;width:180px;font-size:24px}
.menu{left:420px;top:110px;width:440px}button{display:block;width:100%;height:44px;margin:8px 0;background:#26394f;color:#e8edf5;border:1px solid #52677e;border-radius:4px;font-size:16px}
button:hover{background:#426884;color:#ffffff}button:active{background:#628ca6}
.inventory{left:180px;top:60px;width:920px;height:610px}.items{height:500px;overflow-y:scroll;display:grid;grid-template-columns:repeat(8,100px);grid-auto-rows:88px;gap:8px}
.item{padding:8px;background:#25384d;border:1px solid #4a617a;border-radius:4px;font-size:14px}.icon{width:24px;height:24px;background:#d3af69;border-radius:4px;margin-bottom:4px}
.chat{left:24px;bottom:24px;width:480px}.history{height:280px;overflow-y:scroll;font-size:14px}.message{margin:5px 0}
input{display:block;width:440px;height:36px;margin-top:12px;padding:6px;background:#0e1926;color:#e8edf5;border:1px solid #55718e;font-size:16px}
"""

var failed := false

func require(ok: bool, message: String) -> void:
	if not ok:
		failed = true
		printerr("FAIL game UI benchmark: ", message)

func _initialize() -> void:
	call_deferred("run_bench")

func markup(page: String) -> String:
	if page == "menu":
		return '<div class="panel menu"><h2>Paused</h2><button id="resume">Resume game</button><button id="settings">Settings</button><button>Save game</button><button>Load game</button><button>Return to title</button></div>'
	if page == "inventory":
		var rows := ""
		for i in 96:
			rows += '<div class="item"><div class="icon"></div>Item %02d<p>x %d</p></div>' % [i,i % 9 + 1]
		return '<div class="panel inventory"><h2>Inventory - 96 slots</h2><div id="items" class="items">' + rows + '</div></div>'
	if page == "chat":
		var messages := ""
		for i in 40:
			messages += '<p class="message">Player %d: Meet at the north gate.</p>' % (i % 4 + 1)
		return '<div class="panel chat"><h2>Party chat</h2><div class="history">' + messages + '</div><input id="entry" value="On my way"></div>'
	var party := ""
	for i in 4:
		party += '<div class="member">Teammate %d<div class="track"><div class="fill"></div></div></div>' % (i + 1)
	var actions := ""
	for i in 8:
		actions += '<div class="slot">%d<p>Ready</p></div>' % (i + 1)
	return '<div class="panel vitals"><h2>Ranger - Level 24</h2><p id="health_text">Health 80 / 100</p><div class="track"><div id="health" class="fill"></div></div><div class="track"><div id="mana" class="fill mana"></div></div><div class="track"><div id="stamina" class="fill stamina"></div></div></div><div class="panel objectives"><h2>Objectives</h2><p>Reach the northern outpost</p><p>Find supplies: 2 / 5</p><p>Protect the caravan</p></div><div class="panel party">' + party + '</div><div class="panel actions">' + actions + '</div><div class="panel ammo"><span id="ammo">30 / 120</span></div>'

func statistics(values: Array[float]) -> Dictionary:
	var ordered := values.duplicate()
	ordered.sort()
	var total := 0.0
	for value in values:
		total += value
	return {"mean_ms":total/values.size(),"median_ms":ordered[ordered.size()/2],"p95_ms":ordered[mini(ordered.size()-1,int(ordered.size()*0.95))],"p99_ms":ordered[mini(ordered.size()-1,int(ordered.size()*0.99))],"max_ms":ordered.back()}

func drive(doc: WevaDocument, workload: String, tick: int, points: Array[Vector2]) -> void:
	match workload:
		"hud_bound":
			var health := 40 + tick % 60
			doc.data = {"Health":health,"Mana":health-10,"Stamina":health+1,"Ammo":health%30}
		"hud_active", "hud_redundant":
			var health := 80 if workload == "hud_redundant" else 40 + tick % 60
			require(doc.set_element_style("#health","width","%d%%" % health),"health target")
			require(doc.set_element_style("#mana","width","%d%%" % (health - 10)),"mana target")
			require(doc.set_element_style("#stamina","width","%d%%" % (health + 1)),"stamina target")
			# Numeric labels at 10 Hz; bars at 60 Hz. Redundant models simulate
			# games that push the same state on every frame.
			if tick % 6 == 0 or workload == "hud_redundant":
				require(doc.set_element_text("#health_text","Health %d / 100" % health),"health label")
				require(doc.set_element_text("#ammo","%02d / 120" % (health % 30)),"ammo label")
		"menu_hover":
			doc.set_pointer(points[(tick / 6) as int % 2],0)
		"menu_animation":
			# A typical opening/closing opacity transition, kept moving.
			if tick % 30 == 0:
				doc.set_element_style(".menu","opacity","0.35" if tick % 60 == 0 else "1")
		"inventory_scroll":
			# Oscillate within the scrollable area; never time a clamped no-op.
			doc.set_element_scroll("#items",Vector2(0,20 + (tick % 60) * 7))
		"chat_typing":
			if tick % 6 == 0:
				if tick % 120 == 0:
					doc.select_all()
					doc.send_text("On my way ")
				else:
					doc.send_text("a")
	doc.update_document(1.0/60.0)

func run_bench() -> void:
	Engine.max_fps = 0
	if DisplayServer.get_name() != "headless":
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	root.size = Vector2i(1280,720)
	var frames := 600
	var warmups := 120
	if OS.has_environment("WEVA_GAME_BENCH_FRAMES"):
		frames = maxi(1,int(OS.get_environment("WEVA_GAME_BENCH_FRAMES")))
	if OS.has_environment("WEVA_GAME_BENCH_WARMUPS"):
		warmups = maxi(0,int(OS.get_environment("WEVA_GAME_BENCH_WARMUPS")))
	var output := OS.get_environment("WEVA_GAME_BENCH_OUT")
	if not output.is_empty():
		DirAccess.make_dir_recursive_absolute(output)
	var results: Array[Dictionary] = []
	for workload in ["empty", "hud_idle", "hud_active", "hud_redundant", "hud_bound", "menu_hover", "menu_animation", "inventory_scroll", "chat_typing"]:
		var only := OS.get_environment("WEVA_GAME_BENCH_ONLY")
		if not only.is_empty() and workload != only:
			continue
		var page: String = workload.split("_")[0]
		var doc: WevaDocument = null
		var points: Array[Vector2] = []
		if workload != "empty":
			doc = WevaDocument.new()
			doc.document_size = Vector2(1280,720)
			doc.interactive = false
			doc.css = CSS
			doc.html = markup(page)
			if workload == "menu_animation":
				doc.css += '.menu{transition:opacity 0.5s linear}'
			if workload == "hud_bound":
				doc.html = doc.html.replace('id="health"','id="health" style="width:{{Health}}%"').replace('id="mana"','id="mana" style="width:{{Mana}}%"').replace('id="stamina"','id="stamina" style="width:{{Stamina}}%"').replace('Health 80 / 100','Health {{Health}} / 100').replace('30 / 120','{{Ammo}} / 120')
				doc.data = {"Health":80,"Mana":70,"Stamina":81,"Ammo":20}
			root.add_child(doc)
			doc.set_process(false) # Exactly one explicit simulation tick below.
			doc.update_document(0)
			require(doc.get_draw_count() > 0,workload + " must draw")
			if page == "menu":
				for selector in ["#resume","#settings"]:
					var bounds := doc.query_bounds(selector)
					require(bounds.has_area(),selector + " bounds")
					points.append(bounds.get_center())
			if page == "inventory":
				require(doc.get_element_scroll_max("#items").y > 440,"inventory must keep scrolling")
			if page == "chat":
				require(doc.set_focus("#entry"),"chat focus")
			if not output.is_empty():
				FileAccess.open(output.path_join(page + ".html"),FileAccess.WRITE).store_string(doc.html)
				FileAccess.open(output.path_join(page + ".css"),FileAccess.WRITE).store_string(CSS)
		var cpu: Array[float] = []
		var update: Array[float] = []
		var wall: Array[float] = []
		var changed: Array[float] = []
		var idle: Array[float] = []
		await process_frame
		for tick in warmups + frames:
			var start := Time.get_ticks_usec()
			if doc != null:
				drive(doc,workload,tick,points)
			var elapsed := (Time.get_ticks_usec()-start)/1000.0
			var last_update := 0.0 if doc == null else doc.get_last_update_ms()
			await process_frame # Includes the scheduled _draw and native renderer.
			if tick >= warmups:
				cpu.append(elapsed)
				update.append(last_update)
				wall.append((Time.get_ticks_usec()-start)/1000.0)
				if tick % 6 == 0:
					changed.append(elapsed)
				else:
					idle.append(elapsed)
		if doc != null:
			if page == "inventory":
				require(doc.get_element_scroll("#items").y > 0,"scroll took effect")
			if workload == "hud_active":
				require(doc.query_bounds("#health").size.x != doc.query_bounds("#mana").size.x,"bar widths changed")
			if page == "chat":
				require(doc.get_element_value("#entry").length() > 10,"typing took effect")
			if workload == "menu_hover":
				var target := "#resume" if (((warmups+frames-1)/6) as int % 2) == 0 else "#settings"
				require(doc.get_computed_style(target,"background-color") == "#426884","hover style took effect")
			if workload == "hud_bound":
				require(doc.get_element_text("#health_text") == "Health %d / 100" % (40+(warmups+frames-1)%60),"binding applied")
			if not output.is_empty() and DisplayServer.get_name() != "headless":
				await RenderingServer.frame_post_draw
				require(root.get_texture().get_image().save_png(output.path_join(workload + ".png")) == OK,"capture")
		var result := {"workload":workload,"frames":frames,"warmups":warmups,"api_cpu":statistics(cpu),"last_core_update":statistics(update),"whole_frame":statistics(wall),"elements":0 if doc == null else doc.count_elements("*"),"draws":0 if doc == null else doc.get_draw_count(),"triangles":0 if doc == null else doc.get_triangle_count()}
		if not changed.is_empty():
			result["sixth_frame_api_cpu"] = statistics(changed)
		if not idle.is_empty():
			result["other_frame_api_cpu"] = statistics(idle)
		results.append(result)
		print("GAME_UI ",JSON.stringify(result))
		if doc != null:
			doc.free()
		await process_frame
	var report := {"engine":Engine.get_version_info(),"display":DisplayServer.get_name(),"renderer":RenderingServer.get_current_rendering_method(),"adapter":RenderingServer.get_video_adapter_name(),"viewport":[1280,720],"simulated_dt":1.0/60.0,"passed":not failed,"results":results}
	if not output.is_empty():
		FileAccess.open(output.path_join("results.json"),FileAccess.WRITE).store_string(JSON.stringify(report,"\t"))
	print("GAME_UI_COMPLETE ",not failed)
	quit(1 if failed else 0)
