// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Exec;

internal class ExecResourceManager
{
    private readonly ILogger _logger;
    private readonly ExecOptions _execOptions;
    private readonly DistributedApplicationModel _model;

    private readonly ResourceLoggerService _resourceLoggerService;
    private readonly ResourceNotificationService _resourceNotificationService;

    private readonly TaskCompletionSource<IResource> _execResourceInitialized = new();

    public ExecResourceManager(
        ILogger<ExecResourceManager> logger,
        IOptions<ExecOptions> execOptions,
        DistributedApplicationModel model,
        ResourceLoggerService resourceLoggerService,
        ResourceNotificationService resourceNotificationService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _execOptions = execOptions.Value;

        _resourceLoggerService = resourceLoggerService ?? throw new ArgumentNullException(nameof(resourceLoggerService));
        _resourceNotificationService = resourceNotificationService ?? throw new ArgumentNullException(nameof(resourceNotificationService));
    }

    public async IAsyncEnumerable<CommandOutput> StreamExecResourceLogs([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_execOptions.Enabled)
        {
            yield break;
        }

        string type = "waiting";

        yield return new CommandOutput
        {
            Text = $"Waiting for resources to be initialized...",
            Type = type
        };

        // wait until AppHost eventing fires ConfigureExecResource()
        // and execResource is initialized
        IResource? execResource = null;
        Exception? execResourceInitializationException = null;
        try
        {
            execResource = await _execResourceInitialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cancelled before exec resource was initialized.");
            yield break;
        }
        catch (Exception ex)
        {
            _logger.LogError(execResourceInitializationException, "Exec resource initialization failed.");
            execResourceInitializationException = ex;
        }

        if (execResourceInitializationException is not null)
        {
            yield return new CommandOutput
            {
                Text = execResourceInitializationException.Message,
                IsErrorMessage = true,
                Type = type
            };
            yield break;
        }

        // dcp annotation is populated by other handler of BeforeStartEvent
        var dcpExecResourceName = execResource!.GetResolvedResourceName();

        yield return new CommandOutput
        {
            Text = $"Aspire exec starting...",
            Type = type
        };

        // in the background wait for the exec resource to be running to change log type
        _ = Task.Run(async () =>
        {
            await _resourceNotificationService.WaitForResourceAsync(execResource!.Name, targetState: KnownResourceStates.Running, cancellationToken).ConfigureAwait(false);
            type = "running";
        }, cancellationToken);

        // in the background wait for the exec resource to reach terminal state. Once done we can complete logging
        _ = Task.Run(async () =>
        {
            await _resourceNotificationService.WaitForResourceAsync(execResource!.Name, targetStates: KnownResourceStates.TerminalStates, cancellationToken).ConfigureAwait(false);

            // hack: https://github.com/microsoft/aspire/issues/10245
            // workarounds the race-condition between streaming all logs from the resource, and resource completion
            await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);

            _resourceLoggerService.Complete(dcpExecResourceName); // complete stops the `WatchAsync` async-foreach below
        }, cancellationToken);

        await foreach (var logs in _resourceLoggerService.WatchAsync(dcpExecResourceName).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var log in logs)
            {
                yield return new CommandOutput
                {
                    Text = log.Content,
                    IsErrorMessage = log.IsErrorMessage,
                    LineNumber = log.LineNumber,
                    Type = type
                };
            }
        }

        if (!_resourceNotificationService.TryGetCurrentState(dcpExecResourceName, out var resourceEvent))
        {
            yield break;
        }

        int? exitCode;
        if ((exitCode = resourceEvent?.Snapshot?.ExitCode) is not null)
        {
            yield return new CommandOutput
            {
                Text = "Aspire exec exit code: " + exitCode.Value,
                IsErrorMessage = false,
                Type = "exitCode",
                ExitCode = exitCode.Value
            };
        }

        if (resourceEvent?.Snapshot.State == KnownResourceStates.FailedToStart)
        {
            yield return new CommandOutput
            {
                Text = "Aspire exec failed to start",
                IsErrorMessage = true,
                Type = "failedToStart",
                ExitCode = -1 // -1 indicates a failure
            };
        }
    }

    public IResource? CreateExecResource()
    {
        if (!_execOptions.Enabled)
        {
            return null;
        }

        try
        {
            var targetResource = _model.Resources.FirstOrDefault(x => x.Name.Equals(_execOptions.ResourceName, StringComparisons.ResourceName));
            if (targetResource is null)
            {
                _logger.LogError("Target resource '{ResourceName}' not found in the model resources.", _execOptions.ResourceName);
                throw new InvalidOperationException($"Target resource {_execOptions.ResourceName} not found in the model resources");
            }

            var execResource = BuildResource(targetResource);

            _logger.LogDebug("Resource '{ResourceName}' has been successfully built and added to the model resources.", execResource.Name);
            _execResourceInitialized.SetResult(execResource);
            return execResource;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create exec resource.");
            _execResourceInitialized.SetException(ex);
            return null;
        }
    }

    IResource BuildResource(IResource targetExecResource)
    {
        return targetExecResource switch
        {
            ProjectResource prj => BuildAgainstResource(prj),
            ContainerResource container => BuildAgainstResource(container),
            _ => throw new InvalidOperationException($"Target resource {targetExecResource.Name} does not support exec mode.")
        };
    }

    private IResource BuildAgainstResource(ProjectResource project)
    {
        var projectMetadata = project.GetProjectMetadata();
        var projectDir = Path.GetDirectoryName(projectMetadata.ProjectPath) ?? throw new InvalidOperationException("Project path is invalid.");
        var (exe, args) = ParseCommand();

        string execResourceName = project.Name + "-exec";
        var executable = new ExecutableResource(execResourceName, exe, projectDir);
        if (args is not null && args.Length > 0)
        {
            executable.Annotations.Add(new CommandLineArgsCallbackAnnotation((c) =>
            {
                c.Args.AddRange(args);
                return Task.CompletedTask;
            }));
        }

        // take all applicable annotations from target resource to replicate the environment
        foreach (var annotation in project.Annotations.Where(annotation =>
            annotation is EnvironmentAnnotation or EnvironmentCallbackAnnotation
                       or ResourceRelationshipAnnotation or WaitAnnotation))
        {
            executable.Annotations.Add(annotation);
        }

        if (_execOptions.StartResource)
        {
            _logger.LogDebug("Exec resource '{ResourceName}' will wait until project '{Project}' starts up.", execResourceName, project.Name);
            executable.Annotations.Add(new WaitAnnotation(project, waitType: WaitType.WaitUntilHealthy));
        }

        _logger.LogDebug("Exec resource '{ResourceName}' will run command '{Command}' with {ArgsCount} args '{Args}'.", execResourceName, exe, args?.Length ?? 0, string.Join(' ', args ?? []));

        return executable;

        (string exe, string[] args) ParseCommand()
        {
            // cli wraps the command into the string with quotes
            // to keep the command as a single argument
            var command = _execOptions.Command;
            var commandUnwrapped = command.AsSpan(1, command.Length - 2).ToString();
            Debug.Assert(command[0] == '"' && command[^1] == '"');

            return CommandLineArgsParser.ParseCommand(commandUnwrapped);
        }
    }

    private IResource BuildAgainstResource(ContainerResource container)
    {
        var (exe, args) = ParseCommand();
        string execResourceName = container.Name + "-exec";
        var workingDirectory = _execOptions.WorkingDirectory;

        // we cant resolve dcp name of container resource here - too early in the startup pipeline
        // it will be resolved later in the Dcp layer
        var containerExecutable = new ContainerExecutableResource(execResourceName, container, exe, workingDirectory: workingDirectory)
        {
            Args = args
        };

        containerExecutable.Annotations.Add(new WaitAnnotation(container, waitType: WaitType.WaitUntilHealthy));

        _logger.LogDebug("Exec container resource '{ResourceName}' will run command '{Command}' with {ArgsCount} args '{Args}'.", execResourceName, exe, args?.Length ?? 0, string.Join(' ', args ?? []));
        return containerExecutable;

        (string exe, string[] args) ParseCommand()
        {
            // cli wraps the command into the string with quotes
            // to keep the command as a single argument
            var command = _execOptions.Command;
            var commandUnwrapped = command.AsSpan(1, command.Length - 2).ToString();
            Debug.Assert(command[0] == '"' && command[^1] == '"');

            return CommandLineArgsParser.ParseCommand(commandUnwrapped);
        }
    }
}
