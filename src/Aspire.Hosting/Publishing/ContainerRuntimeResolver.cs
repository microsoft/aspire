// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.Dcp;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Resolves the container runtime asynchronously using explicit configuration or auto-detection.
/// Caches the result after first resolution.
/// </summary>
internal sealed class ContainerRuntimeResolver : IContainerRuntimeResolver
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<DcpOptions> _dcpOptions;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Task<IContainerRuntime>? _cachedTask;

    public ContainerRuntimeResolver(
        IServiceProvider serviceProvider,
        IOptions<DcpOptions> dcpOptions,
        ILoggerFactory loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _dcpOptions = dcpOptions;
        _logger = loggerFactory.CreateLogger("Aspire.Hosting.ContainerRuntime");
    }

    public Task<IContainerRuntime> ResolveAsync(CancellationToken cancellationToken = default)
    {
        // Caching behavior:
        // - Completed successfully: return cached result. Caller's token is irrelevant.
        // - In-progress: return the in-flight task (started with a previous caller's token).
        //   If this caller is cancelled, their await throws but the detection continues.
        // - Faulted (e.g. bad ASPIRE_CONTAINER_RUNTIME value): return faulted task.
        //   Config won't change mid-process, so retry won't help.
        // - Cancelled: discard and retry with the new caller's token, since a different
        //   caller may have a valid token.
        // - Null: first call, start detection with this caller's token.
        var task = _cachedTask;
        if (task is not null && !task.IsCanceled)
        {
            return task;
        }

        lock (_lock)
        {
            task = _cachedTask;
            if (task is not null && !task.IsCanceled)
            {
                return task;
            }

            _cachedTask = ResolveInternalAsync(cancellationToken);
            return _cachedTask;
        }
    }

    private async Task<IContainerRuntime> ResolveInternalAsync(CancellationToken cancellationToken)
    {
        var configuredRuntime = _dcpOptions.Value.ContainerRuntime;

        if (configuredRuntime is not null)
        {
            _logger.LogInformation("Container runtime '{RuntimeKey}' configured via ASPIRE_CONTAINER_RUNTIME.", configuredRuntime);
            return _serviceProvider.GetRequiredKeyedService<IContainerRuntime>(configuredRuntime);
        }

        // Auto-detect: probe available runtimes asynchronously.
        // See https://github.com/microsoft/dcp/blob/main/internal/containers/runtimes/runtime.go
        var detected = await ContainerRuntimeDetector.FindAvailableRuntimeAsync(logger: _logger, cancellationToken: cancellationToken).ConfigureAwait(false);
        var runtimeKey = detected?.Executable ?? "docker";

        if (detected is { IsHealthy: true })
        {
            _logger.LogInformation("Container runtime auto-detected: {RuntimeName} ({Executable}).", detected.Name, detected.Executable);
        }
        else if (detected is { IsInstalled: true })
        {
            _logger.LogWarning("Container runtime '{RuntimeName}' is installed but not running. {Error}", detected.Name, detected.Error);
        }
        else
        {
            _logger.LogWarning("No container runtime detected, defaulting to 'docker'. Install Docker or Podman to use container features.");
        }

        return _serviceProvider.GetRequiredKeyedService<IContainerRuntime>(runtimeKey);
    }
}
