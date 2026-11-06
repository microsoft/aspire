// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Fixture for DotnetWatchContractTests: launched by the real watch server through the real watch host proxy (Aspire.Hosting.Dotnet.WatchHost).
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

Console.WriteLine($"contract-args: {string.Join('|', args)}");
Console.WriteLine($"contract-env: CONTRACT_VALUE={Environment.GetEnvironmentVariable("CONTRACT_VALUE") ?? "(unset)"}");
Console.WriteLine($"contract-env: CONTRACT_UNLISTED={Environment.GetEnvironmentVariable("CONTRACT_UNLISTED") ?? "(unset)"}");

app.MapGet("/", () => Greeting.Message());
app.MapGet("/crash", () => { Environment.Exit(42); return ""; });

app.Run();
