// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Terminals.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// Add a terminal demo resource that runs a Hex1b-hosted shell session.
// The terminal host process receives the UDS path via environment variable,
// creates a Hex1b terminal with a custom presentation adapter, and bridges
// client connections over the socket to the shell's PTY.
// This demonstrates the full WithTerminal flow without requiring DCP PTY support.
builder.AddTerminalDemo("demo-shell");

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
