extends Node3D
## Deterministic shared CPU/GPU load for UI measurements, not a game benchmark.

const INSTANCE_COUNT := 480
var movers: Array[MeshInstance3D] = []
var origins: Array[Vector3] = []
var camera: Camera3D

func _ready() -> void:
	var environment := WorldEnvironment.new()
	var settings := Environment.new()
	settings.background_mode = Environment.BG_COLOR
	settings.background_color = Color("829d9b")
	settings.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	settings.ambient_light_color = Color("c2bc98")
	settings.ambient_light_energy = 0.45
	environment.environment = settings
	add_child(environment)
	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-48, -30, 0)
	sun.light_color = Color("ffdc9d")
	sun.light_energy = 1.4
	sun.shadow_enabled = true
	add_child(sun)
	var ground := MeshInstance3D.new()
	var plane := PlaneMesh.new()
	plane.size = Vector2(180, 180)
	ground.mesh = plane
	var sand := StandardMaterial3D.new()
	sand.albedo_color = Color("9b885e")
	sand.roughness = 0.95
	ground.material_override = sand
	add_child(ground)
	var rock := SphereMesh.new()
	rock.radial_segments = 16
	rock.rings = 8
	var materials: Array[StandardMaterial3D] = []
	for shade in ["77694f", "8c7655", "a38964", "625f49"]:
		var material := StandardMaterial3D.new()
		material.albedo_color = Color(shade)
		material.roughness = 0.85
		materials.append(material)
	var random := RandomNumberGenerator.new()
	random.seed = 57219
	for index in INSTANCE_COUNT:
		var instance := MeshInstance3D.new()
		instance.mesh = rock
		instance.material_override = materials[index % materials.size()]
		instance.position = Vector3(random.randf_range(-45, 45), 0.7, random.randf_range(-45, 45))
		instance.scale = Vector3(random.randf_range(1.1, 3.5), random.randf_range(0.6, 2.5), random.randf_range(1.1, 3.5))
		add_child(instance)
		if index < 96:
			movers.append(instance)
			origins.append(instance.position)
	for index in 4:
		var light := OmniLight3D.new()
		light.position = Vector3((index - 1.5) * 14, 4, -5)
		light.omni_range = 20
		light.light_energy = 2
		light.light_color = Color("ffac58")
		add_child(light)
	camera = Camera3D.new()
	camera.far = 220
	add_child(camera)
	camera.current = true
	set_step(0)

func set_step(frame: int) -> void:
	var phase := float(frame) / 60.0
	camera.position = Vector3(sin(phase * 0.12) * 8, 19, 43)
	camera.look_at(Vector3(0, 0, -6))
	for index in movers.size():
		movers[index].position = origins[index] + Vector3(sin(phase + index) * 0.5, sin(phase * 2 + index) * 0.25, 0)
		movers[index].rotation.y = phase * 0.2 + index
