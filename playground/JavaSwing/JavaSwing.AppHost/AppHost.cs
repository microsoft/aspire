// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

builder.AddJavaApp("swing", "../desktop", "target/swing-desktop-1.0.0.jar")
    .WithMavenBuild("-B", "-ntp", "-DskipTests", "package")
    .WithMainClass("com.example.swing.SwingLauncher");

#if !SKIP_DASHBOARD_REFERENCE
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
