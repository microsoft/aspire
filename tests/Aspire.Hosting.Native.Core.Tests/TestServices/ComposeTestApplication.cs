// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.Native.Authoring;
using NativeIntegrations.DockerCompose;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

internal static class ComposeTestApplication
{
    internal static DistributedApplicationBuilder Create(string root, Action<IResourceBuilder<DockerComposeEnvironmentResource>>? configure = null)
    {
        var builder = DistributedApplication.CreateBuilder(["--operation=publish"], new() { ApplicationRoot = root });
        var environment = builder.AddDockerComposeEnvironment("compose").WithDashboard(false);
        configure?.Invoke(environment);
        var password = builder.AddConfiguredParameter("cache-password", secret: true);
        var cache = builder.AddContainer("cache", "redis:7.4-alpine")
            .WithEnvironment("REDIS_PASSWORD", password)
            .WithEntrypoint("/bin/sh")
            .WithArgs("-c", "exec redis-server --requirepass \"$REDIS_PASSWORD\" --appendonly yes --appendfsync always --dir /data")
            .WithEndpoint("tcp", 6379, "redis")
            .WithVolume("cache-data", "/data")
            .WithComputeEnvironment(environment)
            .PublishAsDockerComposeService(new DockerComposeServiceOptions
            {
                Healthcheck = new(["CMD-SHELL", "REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli ping | grep -q PONG"], "1s", "3s", 30)
            });
        cache.WithConnectionString(ReferenceExpression.Create("redis://:", password.AsReference().EncodeUri(), "@",
            cache.GetEndpoint("tcp").HostAndPort));
        builder.AddExecutable("web", Path.Combine(root, "deployment-only", "node"), Path.Combine(root, "web"))
            .WithArgs("server.mts")
            .WithReference(cache)
            .WithEndpoint("http", 8080, "http")
            .WithExternalHttpEndpoints()
            .WaitFor(cache)
            .WithComputeEnvironment(environment)
            .PublishAsDockerComposeService(new DockerComposeServiceOptions
            {
                Image = "aspire-v2-compose-csharp-test:local", Build = new("./web", "./web/Dockerfile"),
                Healthcheck = new(["CMD", "node", "-e", "fetch('http://127.0.0.1:8080/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"], "1s", "3s", 30)
            });

        return builder;
    }
}

internal sealed class ComposeTestRunner
{
    internal ConcurrentQueue<(string Executable, string[] Arguments, DockerCommandOptions Options)> Calls { get; } = new();
    internal string Status { get; set; } = """
        [
          {"Service":"cache","State":"running","Health":"healthy","Publishers":[]},
          {"Service":"web","State":"running","Health":"healthy","Publishers":[{"URL":"127.0.0.1","TargetPort":8080,"PublishedPort":49152,"Protocol":"tcp"}]}
        ]
        """;
    internal string? ApplyFailure { get; set; }

    internal Task<DockerCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, DockerCommandOptions options)
    {
        Calls.Enqueue((executable, arguments.ToArray(), options));
        if (arguments.Contains("up") && ApplyFailure is { } failure)
        {
            return Task.FromResult(new DockerCommandResult(1, "", failure));
        }
        var stdout = arguments.Contains("version") ? "2.40.3" : arguments.Contains("ps") ? Status : "{}";

        return Task.FromResult(new DockerCommandResult(0, stdout, ""));
    }
}
