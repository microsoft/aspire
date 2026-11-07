// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

internal sealed record DotnetWatchResolvedPayload(string ToolPath, string WatchHostPath);

/// <summary>
/// Evaluates SDK-bound inputs and validates the payload before transferring build ownership.
/// </summary>
internal interface IDotnetWatchPreflight
{
    Task<DotnetWatchProjectGraphResult> EvaluateAsync(
        string path, string? configuration, IReadOnlyDictionary<string, string> environment,
        bool globalProperties, string workingDirectory, CancellationToken cancellationToken);

    Task<DotnetWatchResolvedPayload> ResolvePayloadAsync(
        DotnetWatchProjectGraphResult graph, IDistributedApplicationBuilder builder,
        ILogger logger, CancellationToken cancellationToken);
}

internal sealed class DotnetWatchPreflight : IDotnetWatchPreflight
{
    public Task<DotnetWatchProjectGraphResult> EvaluateAsync(
        string path, string? configuration, IReadOnlyDictionary<string, string> environment,
        bool globalProperties, string workingDirectory, CancellationToken cancellationToken) =>
        DotnetWatchProjectGraph.EvaluateGraphAsync(path, configuration, environment, globalProperties, workingDirectory, cancellationToken);

    public async Task<DotnetWatchResolvedPayload> ResolvePayloadAsync(
        DotnetWatchProjectGraphResult graph, IDistributedApplicationBuilder builder,
        ILogger logger, CancellationToken cancellationToken)
    {
        var configured = builder.Configuration[DotnetWatchToolResolver.ToolPathConfigurationKey];
        var overridePath = string.IsNullOrEmpty(configured)
            ? null : Path.GetFullPath(configured, builder.AppHostDirectory);
        var tool = DotnetWatchToolResolver.ResolveToolPath(overridePath,
            DotnetWatchToolResolver.GetToolsDirectory(builder.AppHostAssembly), graph.SdkVersion, logger);
        var host = DotnetWatchToolResolver.ResolveWatchHostPath(
            DotnetWatchToolResolver.GetWatchHostDirectory(builder.AppHostAssembly));
        try
        {
            DotnetWatchPayload.Validate(tool, graph.SdkDirectory);
            _ = DotnetWatchPayload.ValidateWatchHost(host);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new DistributedApplicationException(
                $"The bundled watch payload is incompatible or incomplete: {exception.Message} Restore/rebuild it or run without --watch.", exception);
        }

        await DotnetWatchToolResolver.EnsureRuntimeAvailableAsync(tool, graph.Muxer, graph.SdkVersion, cancellationToken).ConfigureAwait(false);
        await DotnetWatchToolResolver.EnsureRuntimeAvailableAsync(host, graph.Muxer, graph.SdkVersion, cancellationToken).ConfigureAwait(false);
        return new(tool, host);
    }
}
