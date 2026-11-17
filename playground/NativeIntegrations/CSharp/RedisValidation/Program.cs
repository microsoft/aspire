// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Authoring;
using NativeIntegrations.Redis;
using NativeIntegrations.Redis.AppHost;
using NativeIntegrations.Redis.Validation;

var builder = DistributedApplication.CreateBuilder(args);
var password = builder.AddParameter("cache-password", "real-authoring-password");
var cache = builder.AddRedis("cache", password).WithDataVolume(builder.Configuration["data-volume"]);
var consumer = builder.AddExecutable("consumer", builder.Configuration["consumer-path"] ?? throw new ArgumentException("--consumer-path is required."), AppContext.BaseDirectory)
    .WithArgs("--redis-consume", "write")
    .WithReference(cache)
    .WaitFor(cache);
await RedisPersistenceValidation.RunAsync(builder, consumer, args);
