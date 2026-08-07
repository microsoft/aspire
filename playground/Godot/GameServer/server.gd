# Minimal dedicated-server scaffold for the Aspire Godot playground.
# This script is not meant to be run directly; the AppHost starts it via
# `godot --headless --script server.gd` and passes GODOT_SERVER_PORT through
# the environment so the port can be configured without editing this file.
#
# A MainLoop script is the documented way to run Godot without a main scene:
# https://docs.godotengine.org/en/4.3/tutorials/scripting/gdscript/gdscript_basics.html
# SceneTree drives itself once `_initialize` returns, so this script deliberately
# does NOT override `_process`. `MainLoop._process` is declared as
# `_process(delta: float) -> bool` (returning true quits the loop), so a `-> void`
# override is a parse error, and an override that just returns false would only
# re-implement the behaviour SceneTree already provides.
# https://docs.godotengine.org/en/4.3/classes/class_mainloop.html#class-mainloop-private-method-process

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

	# SceneTree exposes the MultiplayerAPI through `get_multiplayer()`. The bare
	# `multiplayer` identifier is a Node property and is not in scope here, so
	# using it fails to parse.
	# https://docs.godotengine.org/en/4.3/classes/class_scenetree.html#class-scenetree-method-get-multiplayer
	get_multiplayer().multiplayer_peer = peer
	print("Godot dedicated server listening on port %d" % port)
