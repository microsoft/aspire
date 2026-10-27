// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var appConfig =
    builder.AddAzureAppConfiguration("appconfig")
    .RunAsEmulator(emulator =>
    {
        emulator.WithDataBindMount();
    });

builder.AddDotnetProject("web", @"..\AzureAppConfiguration.Web\AzureAppConfiguration.Web.csproj")
    .WithReference(appConfig);

builder.Build().Run();
