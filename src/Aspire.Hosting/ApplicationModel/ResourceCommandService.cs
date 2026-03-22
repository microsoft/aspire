// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A service to execute resource commands.
/// </summary>
public class ResourceCommandService
{
    /// <summary>
    /// Maps legacy command names to their current equivalents for backwards compatibility.
    /// </summary>
    private static readonly Dictionary<string, string> s_legacyCommandNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [KnownResourceCommands.LegacyStartCommand] = KnownResourceCommands.StartCommand,
        [KnownResourceCommands.LegacyStopCommand] = KnownResourceCommands.StopCommand,
        [KnownResourceCommands.LegacyRestartCommand] = KnownResourceCommands.RestartCommand,
    };

    private readonly ResourceNotificationService _resourceNotificationService;
    private readonly ResourceLoggerService _resourceLoggerService;
    private readonly IServiceProvider _serviceProvider;

    // Constructor is pureposefully internal so adding new dependencies in the future isn't a public API change.
    internal ResourceCommandService(ResourceNotificationService resourceNotificationService, ResourceLoggerService resourceLoggerService, IServiceProvider serviceProvider)
    {
        _resourceNotificationService = resourceNotificationService;
        _resourceLoggerService = resourceLoggerService;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Execute a command for the specified resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A resource id can be either the unique id of the resource or the displayed resource name.
    /// </para>
    /// <para>
    /// Projects, executables and containers typically have a unique id that combines the display name and a unique suffix. For example, a resource named <c>cache</c> could have a resource id of <c>cache-abcdwxyz</c>.
    /// This id is used to uniquely identify the resource in the app host.
    /// </para>
    /// <para>
    /// The resource name can be also be used to retrieve the resource state, but it must be unique. If there are multiple resources with the same name, then this method will not return a match.
    /// For example, if a resource named <c>cache</c> has multiple replicas, then specifing <c>cache</c> won't return a match.
    /// </para>
    /// </remarks>
    /// <param name="resourceId">The resource id. This id can either exactly match the unique id of the resource or the displayed resource name if the resource name doesn't have duplicates (i.e. replicas).</param>
    /// <param name="commandName">The command name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The <see cref="ExecuteCommandResult" /> indicates command success or failure.</returns>
    public async Task<ExecuteCommandResult> ExecuteCommandAsync(string resourceId, string commandName, CancellationToken cancellationToken = default)
    {
        if (!_resourceNotificationService.TryGetCurrentState(resourceId, out var resourceEvent))
        {
            return new ExecuteCommandResult { Success = false, ErrorMessage = $"Resource '{resourceId}' not found." };
        }

        return await ExecuteCommandCoreAsync(resourceEvent.ResourceId, resourceEvent.Resource, commandName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Execute a command for the specified resource.
    /// </summary>
    /// <param name="resource">The resource. If the resource has multiple instances, such as replicas, then the command will be executed for each instance.</param>
    /// <param name="commandName">The command name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The <see cref="ExecuteCommandResult" /> indicates command success or failure.</returns>
    public async Task<ExecuteCommandResult> ExecuteCommandAsync(IResource resource, string commandName, CancellationToken cancellationToken = default)
    {
        var names = resource.GetResolvedResourceNames();
        // Single resource for IResource. Return its result directly.
        if (names.Length == 1)
        {
            return await ExecuteCommandCoreAsync(names[0], resource, commandName, cancellationToken).ConfigureAwait(false);
        }

        // Run commands for multiple resources in parallel.
        var tasks = new List<Task<ExecuteCommandResult>>();
        foreach (var name in names)
        {
            tasks.Add(ExecuteCommandCoreAsync(name, resource, commandName, cancellationToken));
        }

        // Check for failures and cancellations.
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var failures = new List<(string resourceId, ExecuteCommandResult result)>();
        var cancellations = new List<(string resourceId, ExecuteCommandResult result)>();
        for (var i = 0; i < results.Length; i++)
        {
            if (!results[i].Success)
            {
                if (results[i].Canceled)
                {
                    cancellations.Add((names[i], results[i]));
                }
                else
                {
                    failures.Add((names[i], results[i]));
                }
            }
        }

        if (failures.Count == 0 && cancellations.Count == 0)
        {
            return new ExecuteCommandResult { Success = true };
        }
        else if (failures.Count == 0 && cancellations.Count > 0)
        {
            // All non-successful commands were cancelled
            return new ExecuteCommandResult { Success = false, Canceled = true };
        }
        else
        {
            // There were actual failures (possibly with some cancellations)
            var errorMessage = $"{failures.Count} command executions failed.";
            errorMessage += Environment.NewLine + string.Join(Environment.NewLine, failures.Select(f => $"Resource '{f.resourceId}' failed with error message: {f.result.ErrorMessage}"));

            return new ExecuteCommandResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }
    }

    internal async Task<ExecuteCommandResult> ExecuteCommandCoreAsync(string resourceId, IResource resource, string commandName, CancellationToken cancellationToken)
    {
        var logger = _resourceLoggerService.GetLogger(resourceId);

        logger.LogInformation("Executing command '{CommandName}'.", commandName);

        var annotation = resource.Annotations.OfType<ResourceCommandAnnotation>().SingleOrDefault(a => a.Name == commandName);

        // Backwards compatibility: if the command wasn't found and the caller used a legacy name
        // (e.g. "resource-start"), fall back to the current name (e.g. "start").
        if (annotation is null && s_legacyCommandNameMap.TryGetValue(commandName, out var mappedName))
        {
            logger.LogDebug("Command '{CommandName}' not found, falling back to '{MappedName}'.", commandName, mappedName);
            annotation = resource.Annotations.OfType<ResourceCommandAnnotation>().SingleOrDefault(a => a.Name == mappedName);
            if (annotation is not null)
            {
                commandName = mappedName;
            }
        }

        if (annotation != null)
        {
            try
            {
                var context = new ExecuteCommandContext
                {
                    ResourceName = resourceId,
                    ServiceProvider = _serviceProvider,
                    CancellationToken = cancellationToken
                };

                var result = await annotation.ExecuteCommand(context).ConfigureAwait(false);
                if (result.Success)
                {
                    logger.LogInformation("Successfully executed command '{CommandName}'.", commandName);
                    return result;
                }
                else if (result.Canceled)
                {
                    logger.LogDebug("Command '{CommandName}' was canceled.", commandName);
                    return result;
                }
                else
                {
                    logger.LogInformation("Failure executing command '{CommandName}'. Error message: {ErrorMessage}", commandName, result.ErrorMessage);
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Command '{CommandName}' was canceled.", commandName);
                return CommandResults.Canceled();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error executing command '{CommandName}'.", commandName);
                return new ExecuteCommandResult { Success = false, ErrorMessage = "Unhandled exception thrown." };
            }
        }

        logger.LogInformation("Command '{CommandName}' not available.", commandName);
        return new ExecuteCommandResult { Success = false, ErrorMessage = $"Command '{commandName}' not available for resource '{resourceId}'." };
    }
}
