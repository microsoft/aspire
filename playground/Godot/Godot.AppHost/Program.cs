// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var matchmaker = builder.AddProject<Projects.Godot_Matchmaker>("matchmaker")
    .WithHttpEndpoint()
    .WithExternalHttpEndpoints();

// The Godot game server is a run-mode-only resource.
//
// `WithExplicitStart` only has meaning in run mode: it tells the orchestrator not to launch the
// process until a user triggers it from the dashboard. Publish/deploy has no notion of "start this
// later by hand", so emitting the executable (and the matchmaker's reference to its endpoint) into a
// published manifest would produce a resource that nothing can ever start, plus a service-discovery
// binding to a port that is never allocated. This playground exists to exercise the local
// `AddExecutable` + `WithExplicitStart` path, so the honest model is to omit the resource entirely
// outside run mode rather than invent a publish representation for a manually launched game server.
if (builder.ExecutionContext.IsRunMode)
{
    // Read the Godot binary path from configuration; fall back to a platform-appropriate default.
    // On machines without Godot installed, the godot-server resource is marked WithExplicitStart()
    // so the AppHost starts normally and the resource only runs when manually triggered.
    var configuredGodotBin = builder.Configuration["GODOT_BIN"];
    var godotBin = string.IsNullOrWhiteSpace(configuredGodotBin)
        ? (OperatingSystem.IsWindows() ? "godot.exe" : "godot")
        : configuredGodotBin;

    var godotServer = builder.AddExecutable("godot-server", godotBin, "../GameServer", "--headless", "--script", "server.gd")
        // Expose the UDP game-server port and propagate it as GODOT_SERVER_PORT so the GDScript can
        // read it via OS.get_environment("GODOT_SERVER_PORT") rather than hard-coding a port number.
        .WithEndpoint(env: "GODOT_SERVER_PORT", name: "game",
            protocol: System.Net.Sockets.ProtocolType.Udp, isProxied: false);

    // WithExplicitStart prevents the AppHost from failing on machines without Godot installed.
    // Start this resource manually from the dashboard after setting GODOT_BIN or installing Godot on PATH.
    godotServer.WithExplicitStart();

    matchmaker.WithReference(godotServer.GetEndpoint("game"));
}

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
