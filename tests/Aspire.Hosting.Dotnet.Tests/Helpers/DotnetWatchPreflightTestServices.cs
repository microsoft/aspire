// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dotnet.Watch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Tests.Helpers;

/// <summary>
/// Supplies evaluated identities to model-only tests; real SDK evaluation has separate coverage.
/// </summary>
internal sealed class DotnetWatchPreflightTestServices : IDotnetWatchPreflight
{
    internal Func<string, bool, IReadOnlyDictionary<string, string>, DotnetWatchProjectGraphResult, DotnetWatchProjectGraphResult>? Transform { get; set; }
    internal Func<string, bool, IReadOnlyDictionary<string, string>, string, DotnetWatchProjectGraphResult, DotnetWatchProjectGraphResult>? TransformWithWorkingDirectory { get; set; }
    internal string? PayloadFailure { get; set; }
    internal Func<CancellationToken, Task>? BeforeEvaluation { get; set; }
    private int _evaluationCount;
    internal int EvaluationCount => Volatile.Read(ref _evaluationCount);
    internal static void Configure(IDistributedApplicationBuilder builder)
    {
        if (!builder.Services.Any(descriptor => descriptor.ImplementationInstance is DotnetWatchPreflightTestServices))
        {
            builder.Services.AddSingleton<IDotnetWatchPreflight>(new DotnetWatchPreflightTestServices());
        }
    }

    public async Task<DotnetWatchProjectGraphResult> EvaluateAsync(
        string path, string? configuration, IReadOnlyDictionary<string, string> environment,
        bool globalProperties, string workingDirectory, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _evaluationCount);
        if (BeforeEvaluation is { } beforeEvaluation)
        {
            await beforeEvaluation(cancellationToken);
        }
        path = DotnetWatchProjectGraph.Normalize(path);
        var directory = Path.GetDirectoryName(path)!;
        var selectedConfiguration = configuration ?? environment.GetValueOrDefault("Configuration") ?? "Debug";
        var properties = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = selectedConfiguration,
            ["TargetFramework"] = "net10.0",
            ["TargetFrameworkIdentifier"] = ".NETCoreApp",
            ["TargetFrameworkVersion"] = "v10.0",
            ["OutputType"] = "Exe",
            ["Optimize"] = "false",
            ["TargetPath"] = Path.Combine(directory, "bin", selectedConfiguration, "net10.0", "Service.dll")
        };
        var node = new DotnetWatchProjectNode(path, properties, [], [path],
            [Path.Combine(directory, "bin"), Path.Combine(directory, "obj")]);
        var result = new DotnetWatchProjectGraphResult(
            "/model-only-sdk", "/model-only-dotnet", "10.0.403",
            new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer) { [path] = node });
        if (Transform is not null)
        {
            result = Transform(path, globalProperties, environment, result);
        }
        return TransformWithWorkingDirectory is null
            ? result
            : TransformWithWorkingDirectory(path, globalProperties, environment, workingDirectory, result);
    }

    public Task<DotnetWatchResolvedPayload> ResolvePayloadAsync(
        DotnetWatchProjectGraphResult graph, IDistributedApplicationBuilder builder,
        ILogger logger, CancellationToken cancellationToken)
    {
        if (PayloadFailure is { } failure)
        {
            throw new DistributedApplicationException(failure);
        }
        return Task.FromResult(new DotnetWatchResolvedPayload(
            Path.Combine(DotnetWatchToolResolver.GetToolsDirectory(builder.AppHostAssembly)!, DotnetWatchToolResolver.ToolDllName),
            DotnetWatchToolResolver.ResolveWatchHostPath(DotnetWatchToolResolver.GetWatchHostDirectory(builder.AppHostAssembly))));
    }
}
