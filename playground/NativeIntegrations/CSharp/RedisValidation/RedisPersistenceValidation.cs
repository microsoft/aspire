// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Authoring;
using Aspire.Hosting.Native.Client;
using NativeIntegrations.Redis.AppHost;

namespace NativeIntegrations.Redis.Validation;

internal static class RedisPersistenceValidation
{
    public static async Task RunAsync(DistributedApplicationBuilder builder, IResourceBuilder<ExecutableResource> consumer, string[] args)
    {
        await using var application = builder.Build();
        await application.StartAsync();
        await application.WaitForCompletionAsync(consumer);
        var rejected = Compose(args, "real-authoring-password");
        rejected.Builder.AddExecutable("invalid", "not-an-absolute-executable", ".");
        try
        {
            await application.UpdateAsync(rejected.Builder);
            throw new InvalidOperationException("Invalid native declarations must not succeed.");
        }
        catch (NativeCapabilityException exception) when (exception.Code == "INVALID_ARGUMENT")
        {
        }
        await application.WaitForCompletionAsync(consumer);
        var next = Compose(args, "real-authoring-password-rotated");
        await application.UpdateAsync(next.Builder);
        await application.WaitForCompletionAsync(next.Consumer);
        await application.DisposeAsync();
        Console.WriteLine("redis-persistence-ok");
    }

    private static (DistributedApplicationBuilder Builder, IResourceBuilder<ExecutableResource> Consumer) Compose(string[] args, string password)
    {
        var builder = DistributedApplication.CreateBuilder(args);
        var credential = builder.AddParameter("cache-password", password);
        var cache = builder.AddRedis("cache", credential).WithDataVolume(builder.Configuration["data-volume"]);
        var consumer = builder.AddExecutable("consumer", builder.Configuration["consumer-path"] ?? throw new ArgumentException("--consumer-path is required."), AppContext.BaseDirectory)
            .WithArgs("--redis-consume", "read")
            .WithReference(cache)
            .WaitFor(cache);

        return (builder, consumer);
    }
}
