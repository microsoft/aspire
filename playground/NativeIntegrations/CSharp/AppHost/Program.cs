// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.Native.Client;
using Microsoft.Extensions.Logging.Abstractions;
using NativeIntegration.Generated;

var endpoint = IPEndPoint.Parse(args[0]);
var token = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_RPC_TOKEN")
    ?? throw new InvalidOperationException("Set ASPIRE_NATIVE_RPC_TOKEN for the native server.");
var clock = TimeProvider.System;
await using var client = await NativeClient.ConnectAsync(endpoint, token, TimeSpan.FromSeconds(15), clock);
await using var provider = await NativeProviderActivation.StartAsync(client, "example.csharp.external-api",
    args[1], [], new NativeProviderActivationOptions(), clock, NullLogger.Instance, CancellationToken.None);
var session = await client.Server.OpenApplicationAsync();
var composition = await session.StartGenerationAsync();
var apiKey = await composition.AddSecretParameterAsync("catalog-api-key", "example-authoring-key");
var catalog = await composition.AddExternalApiAsync("catalog", "https://catalog.example/");
await catalog.WithApiKeyAsync(apiKey);
Console.WriteLine(await catalog.GetResourceNameAsync());
await session.RetireGenerationAsync(composition);
