# Minimal dedicated-server scaffold for the Aspire Godot playground.
# This script is not meant to be run directly; the AppHost starts it via
# `godot --headless --script server.gd` and passes GODOT_SERVER_PORT through
# the environment so the port can be configured without editing this file.

extends SceneTree

func _initialize() -> void:
	# Read the port from the Aspire-injected environment variable, defaulting to 7000.
	var port_str: String = OS.get_environment("GODOT_SERVER_PORT")
	var port: int = int(port_str) if port_str != "" else 7000

	print("Godot dedicated server starting on port %d" % port)

	var peer := ENetMultiplayerPeer.new()
	var err := peer.create_server(port)
	if err != OK:
		push_error("Failed to create server on port %d: %s" % [port, err])
		quit(1)
		return

	multiplayer.multiplayer_peer = peer
	print("Godot dedicated server listening on port %d" % port)

func _process(_delta: float) -> void:
	pass
