// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREINTERACTION001 // InteractionInput is used to describe resource command arguments.

/// <summary>
/// A service to execute resource commands.
/// </summary>
public class ResourceCommandService
{
    /// <summary>
    /// Maps legacy command names to their current equivalents for backwards compatibility.
    /// </summary>
    private static readonly Dictionary<string, string> s_legacyCommandNameMap = new(StringComparers.CommandName)
    {
        [KnownResourceCommands.LegacyStartCommand] = KnownResourceCommands.StartCommand,
        [KnownResourceCommands.LegacyStopCommand] = KnownResourceCommands.StopCommand,
        [KnownResourceCommands.LegacyRestartCommand] = KnownResourceCommands.RestartCommand,
        [KnownResourceCommands.LegacySetParameterCommand] = KnownResourceCommands.SetParameterCommand,
        [KnownResourceCommands.LegacyDeleteParameterCommand] = KnownResourceCommands.DeleteParameterCommand,
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
        return await ExecuteCommandCoreAsync(
            resourceId,
            commandName,
            new ResourceCommandExecutionOptions { NonInteractive = true },
            cancellationToken).ConfigureAwait(false);
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
    /// <param name="arguments">The invocation arguments supplied to the command callback.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The <see cref="ExecuteCommandResult" /> indicates command success or failure.</returns>
    public async Task<ExecuteCommandResult> ExecuteCommandAsync(string resourceId, string commandName, InteractionInputCollection arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return await ExecuteCommandCoreAsync(
            resourceId,
            commandName,
            new ResourceCommandExecutionOptions
            {
                Arguments = arguments,
                ArgumentsProvided = true,
                NonInteractive = true
            },
            cancellationToken).ConfigureAwait(false);
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
        var arguments = CreateArguments([], argumentValues: null);

        return await ExecuteCommandAsync(resource, commandName, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Execute a command for the specified resource.
    /// </summary>
    /// <param name="resource">The resource. If the resource has multiple instances, such as replicas, then the command will be executed for each instance.</param>
    /// <param name="commandName">The command name.</param>
    /// <param name="arguments">The invocation arguments supplied to the command callback.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The <see cref="ExecuteCommandResult" /> indicates command success or failure.</returns>
    public async Task<ExecuteCommandResult> ExecuteCommandAsync(IResource resource, string commandName, InteractionInputCollection arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var names = resource.GetResolvedResourceNames();
        // Single resource for IResource. Return its result directly.
        if (names.Length == 1)
        {
            return await ExecuteCommandCoreAsync(
                names[0],
                resource,
                commandName,
                arguments,
                argumentsProvided: true,
                nonInteractive: true,
                cancellationToken).ConfigureAwait(false);
        }

        // Run commands for multiple resources in parallel.
        var tasks = new List<Task<ExecuteCommandResult>>();
        foreach (var name in names)
        {
            tasks.Add(ExecuteCommandCoreAsync(
                name,
                resource,
                commandName,
                CloneArguments(arguments),
                argumentsProvided: true,
                nonInteractive: true,
                cancellationToken));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return CreateAggregateResult(names, results);
    }

    private static ExecuteCommandResult CreateAggregateResult(string[] names, ExecuteCommandResult[] results)
    {
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
            var successWithResult = results.FirstOrDefault(r => r.Data is not null);
            return new ExecuteCommandResult
            {
                Success = true,
                Data = successWithResult?.Data
            };
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
            errorMessage += Environment.NewLine + string.Join(Environment.NewLine, failures.Select(f => $"Resource '{f.resourceId}' failed with error message: {f.result.Message}"));

            return new ExecuteCommandResult
            {
                Success = false,
                Message = errorMessage
            };
        }
    }

    internal (InteractionInputCollection Arguments, string? ErrorMessage) CreateCommandArguments(string resourceId, string commandName, IReadOnlyDictionary<string, string?>? argumentValues)
    {
        if (!_resourceNotificationService.TryGetCurrentState(resourceId, out var resourceEvent))
        {
            return (CreateArguments([], argumentValues), null);
        }

        var resolvedCommandName = commandName;
        var annotation = ResolveCommandAnnotation(resourceEvent.Resource, ref resolvedCommandName);
        if (annotation is null)
        {
            return (CreateArguments([], argumentValues), null);
        }

        return CreateCommandArguments(annotation, resolvedCommandName, argumentValues);
    }

    private static (InteractionInputCollection Arguments, string? ErrorMessage) CreateCommandArguments(IResource resource, string commandName, IReadOnlyDictionary<string, string?>? argumentValues)
    {
        var resolvedCommandName = commandName;
        var annotation = ResolveCommandAnnotation(resource, ref resolvedCommandName);
        if (annotation is null)
        {
            return (CreateArguments([], argumentValues), null);
        }

        return CreateCommandArguments(annotation, resolvedCommandName, argumentValues);
    }

    private static (InteractionInputCollection Arguments, string? ErrorMessage) CreateCommandArguments(ResourceCommandAnnotation annotation, string resolvedCommandName, IReadOnlyDictionary<string, string?>? argumentValues)
    {
        if (argumentValues is { Count: > 0 })
        {
            var disabledArgumentNames = annotation.Arguments
                .Where(argument => IsStaticallyDisabled(argument) && argumentValues.ContainsKey(argument.Name))
                .Select(argument => argument.Name)
                .ToArray();
            if (disabledArgumentNames.Length > 0)
            {
                return (CreateArguments(annotation.Arguments, argumentValues), CreateDisabledArgumentMessage(resolvedCommandName, disabledArgumentNames));
            }

            var argumentNames = new HashSet<string>(
                annotation.Arguments.Select(argument => argument.Name),
                StringComparers.InteractionInputName);
            var unknownArgumentNames = argumentValues.Keys
                .Where(argumentName => !argumentNames.Contains(argumentName))
                .ToArray();

            if (unknownArgumentNames.Length > 0)
            {
                return (CreateArguments(annotation.Arguments, argumentValues), CreateUnknownArgumentMessage(resolvedCommandName, unknownArgumentNames));
            }
        }

        return (CreateArguments(annotation.Arguments, argumentValues), null);
    }

    internal (InteractionInputCollection Arguments, string? ErrorMessage) CreateCommandArguments(string resourceId, string commandName, IReadOnlyList<string?>? orderedArgumentValues)
    {
        if (!_resourceNotificationService.TryGetCurrentState(resourceId, out var resourceEvent))
        {
            return (CreateArguments([], orderedArgumentValues), null);
        }

        var resolvedCommandName = commandName;
        var annotation = ResolveCommandAnnotation(resourceEvent.Resource, ref resolvedCommandName);
        if (annotation is null)
        {
            return (CreateArguments([], orderedArgumentValues), null);
        }

        if (orderedArgumentValues is { Count: var argumentCount } && argumentCount > annotation.Arguments.Count)
        {
            return (CreateArguments(annotation.Arguments, orderedArgumentValues: null), $"Command '{resolvedCommandName}' accepts {annotation.Arguments.Count} argument(s), but {argumentCount} were provided.");
        }

        if (orderedArgumentValues is { Count: > 0 })
        {
            var disabledArgumentNames = annotation.Arguments
                .Take(orderedArgumentValues.Count)
                .Where(static argument => IsStaticallyDisabled(argument))
                .Select(static argument => argument.Name)
                .ToArray();
            if (disabledArgumentNames.Length > 0)
            {
                return (CreateArguments(annotation.Arguments, orderedArgumentValues), CreateDisabledArgumentMessage(resolvedCommandName, disabledArgumentNames));
            }
        }

        return (CreateArguments(annotation.Arguments, orderedArgumentValues), null);
    }

    internal async Task<ExecuteCommandResult> ExecuteCommandAsync(string resourceId, string commandName, ResourceCommandExecutionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        return await ExecuteCommandCoreAsync(resourceId, commandName, options, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ExecuteCommandResult> ExecuteCommandAsync(IResource resource, string commandName, IReadOnlyDictionary<string, string?>? argumentValues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var result = CreateCommandArguments(resource, commandName, argumentValues);
        if (result.ErrorMessage is not null)
        {
            return new ExecuteCommandResult { Success = false, Message = result.ErrorMessage };
        }

        return await ExecuteCommandAsync(resource, commandName, result.Arguments, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ExecuteCommandResult> ExecuteCommandCoreAsync(string resourceId, IResource resource, string commandName, InteractionInputCollection arguments, bool argumentsProvided, bool nonInteractive, CancellationToken cancellationToken)
    {
        var logger = _resourceLoggerService.GetLogger(resourceId);

        logger.LogInformation("Executing command '{CommandName}'.", commandName);

        var annotation = ResolveCommandAnnotation(resource, ref commandName, logger);

        if (annotation != null)
        {
            try
            {
                arguments = NormalizeCommandArguments(annotation, arguments);

                HashSet<string>? loadedDynamicArgumentNames = null;
                if (!nonInteractive && !argumentsProvided && annotation.Arguments.Count > 0)
                {
                    var (promptedArguments, promptResult) = await PromptForCommandArgumentsAsync(annotation, arguments, cancellationToken).ConfigureAwait(false);
                    if (promptResult is not null)
                    {
                        return promptResult;
                    }

                    arguments = promptedArguments!;
                }
                else
                {
                    loadedDynamicArgumentNames = await LoadDynamicCommandArgumentsAsync(arguments, cancellationToken).ConfigureAwait(false);
                }

                if (!await ValidateArgumentsAsync(annotation, arguments, loadedDynamicArgumentNames, cancellationToken).ConfigureAwait(false))
                {
                    return new ExecuteCommandResult
                    {
                        Success = false,
                        Message = "Command argument validation failed.",
                        InvalidArguments = arguments
                    };
                }

                var context = new ExecuteCommandContext
                {
                    ResourceName = resourceId,
                    ServiceProvider = _serviceProvider,
                    CancellationToken = cancellationToken,
                    Logger = logger,
                    Arguments = arguments
                };

                // When non-interactive, set an AsyncLocal scope so that IInteractionService.IsAvailable
                // returns false during command execution. This lets command callbacks know they should
                // not attempt to prompt the user.
                using var _ = nonInteractive ? InteractionService.StartNonInteractiveScope() : default;

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
                    logger.LogInformation("Failure executing command '{CommandName}'. Error message: {ErrorMessage}", commandName, result.Message);
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
                return new ExecuteCommandResult { Success = false, Message = ex.Message };
            }
        }

        logger.LogInformation("Command '{CommandName}' not available.", commandName);
        return new ExecuteCommandResult { Success = false, Message = $"Command '{commandName}' not available for resource '{resource.GetResolvedDisplayResourceName(resourceId)}'." };
    }

    internal async Task<(ExecuteCommandResult Result, InteractionInputCollection? Arguments)> ValidateCommandArgumentsAsync(string resourceId, string commandName, InteractionInputCollection arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!_resourceNotificationService.TryGetCurrentState(resourceId, out var resourceEvent))
        {
            return (new ExecuteCommandResult { Success = false, Message = $"Resource '{resourceId}' not found." }, null);
        }

        var resolvedCommandName = commandName;
        var annotation = ResolveCommandAnnotation(resourceEvent.Resource, ref resolvedCommandName);

        if (annotation is null)
        {
            return (new ExecuteCommandResult { Success = false, Message = $"Command '{commandName}' not available for resource '{resourceEvent.Resource.GetResolvedDisplayResourceName(resourceId)}'." }, null);
        }

        var normalizedArguments = NormalizeCommandArguments(annotation, arguments);
        var loadedDynamicArgumentNames = await LoadDynamicCommandArgumentsAsync(normalizedArguments, cancellationToken).ConfigureAwait(false);

        var result = await ValidateArgumentsAsync(annotation, normalizedArguments, loadedDynamicArgumentNames, cancellationToken).ConfigureAwait(false)
            ? CommandResults.Success()
            : new ExecuteCommandResult
            {
                Success = false,
                Message = "Command argument validation failed.",
                InvalidArguments = normalizedArguments
            };

        return (result, normalizedArguments);
    }

    private async Task<ExecuteCommandResult> ExecuteCommandCoreAsync(string resourceId, string commandName, ResourceCommandExecutionOptions options, CancellationToken cancellationToken)
    {
        if (!_resourceNotificationService.TryGetCurrentState(resourceId, out var resourceEvent))
        {
            return new ExecuteCommandResult { Success = false, Message = $"Resource '{resourceId}' not found." };
        }

        var arguments = options.Arguments;
        if (arguments is null)
        {
            var result = CreateCommandArguments(resourceEvent.ResourceId, commandName, options.ArgumentValues);
            if (result.ErrorMessage is not null)
            {
                return new ExecuteCommandResult { Success = false, Message = result.ErrorMessage };
            }

            arguments = result.Arguments;
        }

        return await ExecuteCommandCoreAsync(
            resourceEvent.ResourceId,
            resourceEvent.Resource,
            commandName,
            arguments,
            options.ArgumentsProvided,
            options.NonInteractive,
            cancellationToken).ConfigureAwait(false);
    }

    private static ResourceCommandAnnotation? ResolveCommandAnnotation(IResource resource, ref string commandName, ILogger? logger = null)
    {
        var requestedCommandName = commandName;
        var annotation = resource.Annotations.OfType<ResourceCommandAnnotation>().SingleOrDefault(a => string.Equals(a.Name, requestedCommandName, StringComparisons.CommandName));

        // Backwards compatibility: if the command wasn't found and the caller used a legacy name
        // (e.g. "resource-start"), fall back to the current name (e.g. "start").
        if (annotation is null && s_legacyCommandNameMap.TryGetValue(commandName, out var mappedName))
        {
            logger?.LogDebug("Command '{CommandName}' not found, falling back to '{MappedName}'.", commandName, mappedName);
            annotation = resource.Annotations.OfType<ResourceCommandAnnotation>().SingleOrDefault(a => string.Equals(a.Name, mappedName, StringComparisons.CommandName));
            if (annotation is not null)
            {
                commandName = mappedName;
            }
        }

        return annotation;
    }

    private static string CreateUnknownArgumentMessage(string commandName, string[] unknownArgumentNames)
    {
        return unknownArgumentNames.Length == 1
            ? $"Unknown argument '{unknownArgumentNames[0]}' for command '{commandName}'."
            : $"Unknown arguments for command '{commandName}': {string.Join(", ", unknownArgumentNames.Select(argumentName => $"'{argumentName}'"))}.";
    }

    private static string CreateDisabledArgumentMessage(string commandName, string[] disabledArgumentNames)
    {
        return disabledArgumentNames.Length == 1
            ? $"Argument '{disabledArgumentNames[0]}' for command '{commandName}' is disabled."
            : $"Arguments for command '{commandName}' are disabled: {string.Join(", ", disabledArgumentNames.Select(argumentName => $"'{argumentName}'"))}.";
    }

    private static bool IsStaticallyDisabled(InteractionInput argument)
    {
        // Dynamic inputs often start disabled until their dependencies are filled, for example:
        // category=fruit enables item=banana, then item=banana enables priority=express.
        // Do not reject those submitted values until the load callback has had a chance to
        // update the input's Disabled state.
        return argument.Disabled && argument.DynamicLoading is null;
    }

    private async Task<bool> ValidateArgumentsAsync(ResourceCommandAnnotation annotation, InteractionInputCollection arguments, HashSet<string>? loadedDynamicArgumentNames, CancellationToken cancellationToken)
    {
        foreach (var argument in arguments)
        {
            argument.ValidationErrors.Clear();
        }

        var context = new InputsDialogValidationContext
        {
            Inputs = arguments,
            CancellationToken = cancellationToken,
            Services = _serviceProvider
        };

        foreach (var argument in arguments)
        {
            var value = argument.Value = argument.InputType == InputType.SecretText
                ? argument.Value
                : argument.Value?.Trim();

            if (argument.Disabled)
            {
                // Dynamic loading can leave a dependent input disabled after it has normalized a
                // harmless default/sentinel value, such as Browser Logs using the default profile
                // while Shared mode is off. Only report submitted values for dynamic inputs that
                // never loaded because their dependencies were incomplete, for example
                // priority=express without a selected item.
                if (!string.IsNullOrEmpty(value) && argument.DynamicLoading is not null && loadedDynamicArgumentNames?.Contains(argument.Name) != true)
                {
                    context.AddValidationError(argument, "Argument is disabled.");
                }

                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                if (argument.Required)
                {
                    context.AddValidationError(argument, "Value is required.");
                }

                continue;
            }

            switch (argument.InputType)
            {
                case InputType.Text:
                case InputType.SecretText:
                    var maxLength = InteractionHelpers.GetMaxLength(argument.MaxLength);

                    if (value.Length > maxLength)
                    {
                        context.AddValidationError(argument, $"Value length exceeds {maxLength} characters.");
                    }
                    break;
                case InputType.Choice:
                    if (!argument.AllowCustomChoice && argument.Options is { } options && !options.Any(o => o.Key == value))
                    {
                        context.AddValidationError(argument, "Value must be one of the provided options.");
                    }
                    break;
                case InputType.Boolean:
                    if (!bool.TryParse(value, out _))
                    {
                        context.AddValidationError(argument, "Value must be a valid boolean.");
                    }
                    break;
                case InputType.Number:
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    {
                        context.AddValidationError(argument, "Value must be a valid number.");
                    }
                    break;
                default:
                    break;
            }
        }

        if (!context.HasErrors && annotation.ValidateArguments is { } validateArguments)
        {
            await validateArguments(context).ConfigureAwait(false);
        }

        return !context.HasErrors;
    }

    private async Task<HashSet<string>> LoadDynamicCommandArgumentsAsync(InteractionInputCollection arguments, CancellationToken cancellationToken)
    {
        var loadedArgumentNames = new HashSet<string>(StringComparers.InteractionInputName);
        foreach (var argument in arguments)
        {
            if (argument.DynamicLoading is { } dynamicLoading && ShouldLoadDynamicCommandArgument(dynamicLoading, arguments))
            {
                await dynamicLoading.LoadCallback(new LoadInputContext
                {
                    AllInputs = arguments,
                    Input = argument,
                    Services = _serviceProvider,
                    CancellationToken = cancellationToken
                }).ConfigureAwait(false);
                loadedArgumentNames.Add(argument.Name);
            }
        }

        return loadedArgumentNames;
    }

    private static bool ShouldLoadDynamicCommandArgument(InputLoadOptions dynamicLoading, InteractionInputCollection arguments)
    {
        if (dynamicLoading.AlwaysLoadOnStart || dynamicLoading.DependsOnInputs is not { Count: > 0 } dependencies)
        {
            return true;
        }

        foreach (var dependency in dependencies)
        {
            if (!arguments.TryGetByName(dependency, out var input) || string.IsNullOrEmpty(input.Value))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<(InteractionInputCollection? Arguments, ExecuteCommandResult? Result)> PromptForCommandArgumentsAsync(ResourceCommandAnnotation annotation, InteractionInputCollection arguments, CancellationToken cancellationToken)
    {
        var interactionService = _serviceProvider.GetRequiredService<IInteractionService>();
        if (!interactionService.IsAvailable)
        {
            return (null, new ExecuteCommandResult
            {
                Success = false,
                Message = "Command requires input, but interactive prompting is not available."
            });
        }

        var result = await interactionService.PromptInputsAsync(
            annotation.DisplayName,
            annotation.DisplayDescription,
            CloneArguments(arguments),
            new InputsDialogInteractionOptions
            {
                PrimaryButtonText = annotation.DisplayName,
                ShowDismiss = true,
                ShowSecondaryButton = true,
                ValidationCallback = annotation.ValidateArguments
            },
            cancellationToken).ConfigureAwait(false);

        return result.Canceled
            ? (null, CommandResults.Canceled())
            : (result.Data, null);
    }

    private static InteractionInputCollection CreateArguments(IReadOnlyList<InteractionInput> commandArguments, IReadOnlyDictionary<string, string?>? argumentValues)
    {
        if (commandArguments is not { Count: > 0 })
        {
            return new InteractionInputCollection([]);
        }

        var inputs = new InteractionInput[commandArguments.Count];
        for (var i = 0; i < commandArguments.Count; i++)
        {
            var input = commandArguments[i];
            var value = input.Value;
            if (argumentValues?.TryGetValue(input.Name, out var argumentValue) == true)
            {
                value = argumentValue;
            }

            inputs[i] = CloneInput(input, value);
        }

        return new InteractionInputCollection(inputs);
    }

    private static InteractionInputCollection CreateArguments(IReadOnlyList<InteractionInput> commandArguments, IReadOnlyList<string?>? orderedArgumentValues)
    {
        if (commandArguments is not { Count: > 0 })
        {
            return new InteractionInputCollection([]);
        }

        var inputs = new InteractionInput[commandArguments.Count];
        for (var i = 0; i < commandArguments.Count; i++)
        {
            var input = commandArguments[i];
            var value = orderedArgumentValues is not null && i < orderedArgumentValues.Count
                ? orderedArgumentValues[i]
                : input.Value;

            inputs[i] = CloneInput(input, value);
        }

        return new InteractionInputCollection(inputs);
    }

    private static InteractionInputCollection NormalizeCommandArguments(ResourceCommandAnnotation annotation, InteractionInputCollection arguments)
    {
        if (annotation.Arguments.Count == 0)
        {
            return arguments;
        }

        return CreateArguments(annotation.Arguments, CreateArgumentValues(arguments));
    }

    private static IReadOnlyDictionary<string, string?>? CreateArgumentValues(InteractionInputCollection arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        var values = new Dictionary<string, string?>(StringComparers.InteractionInputName);
        foreach (var argument in arguments)
        {
            values[argument.Name] = argument.Value;
        }

        return values;
    }

    private static InteractionInput CloneInput(InteractionInput input, string? value)
    {
        return new InteractionInput
        {
            Name = input.Name,
            Label = input.Label,
            Description = input.Description,
            EnableDescriptionMarkdown = input.EnableDescriptionMarkdown,
            InputType = input.InputType,
            Required = input.Required,
            Options = input.Options,
            DynamicLoading = input.DynamicLoading,
            Value = value,
            Placeholder = input.Placeholder,
            AllowCustomChoice = input.AllowCustomChoice,
            Disabled = input.Disabled,
            MaxLength = input.MaxLength
        };
    }

    private static InteractionInputCollection CloneArguments(InteractionInputCollection arguments)
    {
        var inputs = new InteractionInput[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            var input = arguments[i];
            inputs[i] = CloneInput(input, input.Value);
        }

        return new InteractionInputCollection(inputs);
    }

}

internal sealed class ResourceCommandExecutionOptions
{
    public InteractionInputCollection? Arguments { get; init; }

    public IReadOnlyDictionary<string, string?>? ArgumentValues { get; init; }

    public bool ArgumentsProvided { get; init; }

    public bool NonInteractive { get; init; }
}

#pragma warning restore ASPIREINTERACTION001
