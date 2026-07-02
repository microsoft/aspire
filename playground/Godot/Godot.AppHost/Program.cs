// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var matchmaker = builder.AddProject<Projects.Godot_Matchmaker>("matchmaker")
    .WithExternalHttpEndpoints();

// Read Godot binary path from configuration; fall back to a platform-appropriate default.
// On machines without Godot installed, the godot-server resource is marked WithExplicitStart()
// so the AppHost starts normally and the resource only runs when manually triggered.
var godotBin = builder.Configuration["GODOT_BIN"]
    ?? (OperatingSystem.IsWindows() ? "godot.exe" : "godot");

var godotServer = builder.AddExecutable("godot-server", godotBin, "../GameServer", "--headless", "--script", "server.gd")
    // Expose the UDP game-server port and propagate it as GODOT_SERVER_PORT so the GDScript can
    // read it via OS.get_environment("GODOT_SERVER_PORT") rather than hard-coding a port number.
    .WithEndpoint(port: 7000, targetPort: 7000, env: "GODOT_SERVER_PORT", name: "game",
        protocol: System.Net.Sockets.ProtocolType.Udp, isProxied: false);

godotServer.WithReference(matchmaker)
           .WaitFor(matchmaker)
           // WithExplicitStart prevents the AppHost from failing on machines without Godot installed.
           // Start this resource manually from the dashboard after setting GODOT_BIN or installing Godot on PATH.
           .WithExplicitStart();

matchmaker.WithReference(godotServer.GetEndpoint("game"));

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
