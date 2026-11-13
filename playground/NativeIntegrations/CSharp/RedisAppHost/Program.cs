// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Authoring;
using NativeIntegrations.Redis;
using NativeIntegrations.Redis.AppHost;

var builder = DistributedApplication.CreateBuilder(args);
var cache = builder.AddRedis("cache").WithDataVolume();
builder.AddExecutable("consumer", builder.Configuration["consumer-path"] ?? throw new ArgumentException("--consumer-path is required."), AppContext.BaseDirectory)
    .WithArgs("--redis-consume", builder.Configuration["operation"] ?? "write")
    .WithReference(cache)
    .WaitFor(cache);
await builder.Build().RunAsync();
