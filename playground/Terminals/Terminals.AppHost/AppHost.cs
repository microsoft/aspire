// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

// A multi-replica project that calls `WithTerminal()` so each replica gets its
// own pseudo-terminal and the dashboard can attach to any of them via
// `/api/terminal?resource=repl&replica=<i>`. The replica index is forwarded as
// an environment variable so the REPL can stamp it on its banner.
builder.AddProject<Projects.Terminals_Repl>("repl")
    .WithReplicas(2)
    .WithEnvironment("ASPIRE_RESOURCE_NAME", "repl")
    .WithTerminal(options =>
    {
        options.Columns = 120;
        options.Rows = 32;
    });

if (OperatingSystem.IsWindows())
{
    // Single-replica executable wrapping cmd.exe to demonstrate that
    // WithTerminal() also works for arbitrary executables, not just projects.
    // DCP's PTY allocator currently only supports Windows, so this branch is
    // gated behind an OS check; future Phase 3 follow-ups will extend it to
    // Linux and macOS.
    builder.AddExecutable("shell", "cmd.exe", ".")
        .WithTerminal();
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

