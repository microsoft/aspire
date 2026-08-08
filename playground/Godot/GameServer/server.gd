extends SceneTree

func _initialize() -> void:
	var port_str: String = OS.get_environment("GODOT_SERVER_PORT")
	var port: int = int(port_str) if port_str != "" else 7000

	print("Godot dedicated server starting on port %d" % port)

	var peer := ENetMultiplayerPeer.new()
	var err := peer.create_server(port)
	if err != OK:
		push_error("Failed to create server on port %d: %s" % [port, err])
		quit(1)
		return

	get_multiplayer().multiplayer_peer = peer
	print("Godot dedicated server listening on port %d" % port)
