#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREUSERSECRETS001

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Handles processing of parameter resources during application orchestration.
/// </summary>
public sealed class ParameterProcessor(
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService,
    IInteractionService interactionService,
    ILogger<ParameterProcessor> logger,
    DistributedApplicationExecutionContext executionContext,
    IDeploymentStateManager deploymentStateManager,
    IUserSecretsManager userSecretsManager)
{
    internal const string SaveToUserSecretsName = "SaveToUserSecrets";
    internal const string DeleteFromUserSecretsName = "DeleteFromUserSecrets";
    internal const string SetParameterValueName = "Value";

    private readonly List<ParameterResource> _unresolvedParameters = [];
    private readonly HashSet<ParameterResource> _observedParameters = [];
    private readonly Dictionary<ParameterResource, SemaphoreSlim> _parameterUpdateLocks = [];
    private readonly object _observedParametersLock = new();
    private readonly object _unresolvedParametersLock = new();
    private readonly object _resolutionTaskLock = new();
    private CancellationTokenSource? _allParametersResolvedCts;
    private Task? _parameterResolutionTask;

    /// <summary>
    /// Initializes parameter resources and handles unresolved parameters if interaction service is available.
    /// </summary>
    /// <param name="parameterResources">The parameter resources to initialize.</param>
    /// <param name="waitForResolution">Whether to wait for all parameters to be resolved before completing the returned Task.</param>
    /// <returns>A task that completes when all parameters are resolved (if waitForResolution is true) or when initialization is complete.</returns>
    public async Task InitializeParametersAsync(IEnumerable<ParameterResource> parameterResources, bool waitForResolution = false)
    {
        // Initialize all parameter resources by setting their WaitForValueTcs.
        // This allows them to be processed asynchronously later.
        foreach (var parameterResource in parameterResources)
        {
            ObserveParameterValueChanges(parameterResource);
            parameterResource.EnsureValueTask();

            await ProcessParameterAsync(parameterResource).ConfigureAwait(false);
        }

        // If interaction service is available, we can handle unresolved parameters.
        // This will allow the user to provide values for parameters that could not be initialized.
        if (interactionService.IsAvailable && HasUnresolvedParameters(_unresolvedParameters))
        {
            // Start the loop that will allow the user to specify values for unresolved parameters.
            var task = EnsureParameterResolutionTaskRunningAsync();

            if (waitForResolution)
            {
                await task.ConfigureAwait(false);
            }
        }
    }

    private Task EnsureParameterResolutionTaskRunningAsync()
    {
        lock (_resolutionTaskLock)
        {
            if (_parameterResolutionTask is null || _parameterResolutionTask.IsCompleted)
            {
                var cts = new CancellationTokenSource();
                _allParametersResolvedCts = cts;
                _parameterResolutionTask = Task.Run(async () =>
                {
                    try
                    {
                        await HandleUnresolvedParametersAsync(_unresolvedParameters, cts.Token).ConfigureAwait(false);
                        logger.LogDebug("All unresolved parameters have been handled successfully.");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to handle unresolved parameters.");
                    }
                });
            }

            return _parameterResolutionTask;
        }
    }

    /// <summary>
    /// Initializes parameter resources by collecting dependent parameters from the distributed application model
    /// and handles unresolved parameters if interaction service is available.
    /// </summary>
    /// <param name="model">The distributed application model to collect parameters from.</param>
    /// <param name="waitForResolution">Whether to wait for all parameters to be resolved before completing the returned Task.</param>
    /// <param name="cancellationToken">The cancellation token to observe while waiting for parameters to be resolved.</param>
    /// <returns>A task that completes when all parameters are resolved (if waitForResolution is true) or when initialization is complete.</returns>
    public async Task InitializeParametersAsync(DistributedApplicationModel model, bool waitForResolution = false, CancellationToken cancellationToken = default)
    {
        var referencedParameters = new Dictionary<string, ParameterResource>();

        await CollectDependentParameterResourcesAsync(model, referencedParameters, cancellationToken).ConfigureAwait(false);

        // Combine explicit parameters with dependent parameters
        var explicitParameters = model.Resources.OfType<ParameterResource>();
        var dependentParameters = referencedParameters.Values.Where(p => !explicitParameters.Contains(p));
        var allParameters = explicitParameters.Concat(dependentParameters).ToList();

        if (allParameters.Any())
        {
            await InitializeParametersAsync(allParameters, waitForResolution).ConfigureAwait(false);
        }

        // In publish mode, save all parameter values at the end
        if (executionContext.IsPublishMode && allParameters.Any())
        {
            await SaveParametersToDeploymentStateAsync(allParameters, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CollectDependentParameterResourcesAsync(DistributedApplicationModel model, Dictionary<string, ParameterResource> referencedParameters, CancellationToken cancellationToken)
    {
        foreach (var resource in model.Resources)
        {
            if (resource.IsExcludedFromPublish())
            {
                continue;
            }

            var dependencies = await resource.GetResourceDependenciesAsync(executionContext, ResourceDependencyDiscoveryMode.Recursive, cancellationToken).ConfigureAwait(false);
            foreach (var parameter in dependencies.OfType<ParameterResource>())
            {
                referencedParameters.TryAdd(parameter.Name, parameter);
            }
        }
    }

    private async Task ProcessParameterAsync(ParameterResource parameterResource)
    {
        // Add the "Set parameter" command if the app is running and the interaction service is available.
        // This command allows the user to set the parameter value at runtime.
        if (executionContext.IsRunMode && interactionService.IsAvailable && !parameterResource.Annotations.OfType<ResourceCommandAnnotation>().Any(a => a.Name.Equals(KnownResourceCommands.SetParameterCommand, StringComparisons.CommandName)))
        {
            AddSetParameterCommand(parameterResource);
        }

        try
        {
            var value = parameterResource.ValueInternal;

            await parameterResource.SetValueAsync(value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Missing parameter values throw a MissingParameterValueException.
            if (interactionService.IsAvailable && ex is MissingParameterValueException)
            {
                // If interaction service is available, we can prompt the user to provide a value.
                // Add the parameter to unresolved parameters list.
                AddUnresolvedParameter(parameterResource);

                loggerService.GetLogger(parameterResource)
                    .LogWarning("Parameter resource {ResourceName} could not be initialized. Waiting for user input.", parameterResource.Name);
            }
            else
            {
                // If interaction service is not available, we log the error and set the state to error.
                await parameterResource.SetExceptionAsync(ex).ConfigureAwait(false);

                loggerService.GetLogger(parameterResource)
                    .LogError(ex, "Failed to initialize parameter resource {ResourceName}.", parameterResource.Name);
            }

            if (ex is MissingParameterValueException && interactionService.IsAvailable)
            {
                await UpdateParameterStateAsync(parameterResource, ex.Message, new(KnownResourceStates.ValueMissing, KnownResourceStateStyles.Warn)).ConfigureAwait(false);
            }
        }
    }

    private void ObserveParameterValueChanges(ParameterResource parameterResource)
    {
        lock (_observedParametersLock)
        {
            if (!_observedParameters.Add(parameterResource))
            {
                return;
            }

            parameterResource.ValueChanged += OnParameterValueChangedAsync;
        }
    }

    private Task OnParameterValueChangedAsync(ParameterResource parameterResource, ParameterResourceValueChangedEventArgs eventArgs, CancellationToken cancellationToken)
    {
        return HandleParameterValueChangedAsync(parameterResource, eventArgs, cancellationToken);
    }

    private async Task HandleParameterValueChangedAsync(ParameterResource parameterResource, ParameterResourceValueChangedEventArgs eventArgs, CancellationToken cancellationToken)
    {
        var updateLock = GetParameterUpdateLock(parameterResource);
        await updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (eventArgs.Version != parameterResource.ValueChangeVersion)
            {
                return;
            }

            if (eventArgs.Exception is null)
            {
                OnParameterResolved(_unresolvedParameters, parameterResource);
                await UpdateParameterStateAsync(parameterResource, eventArgs.Value, KnownResourceStates.Running).ConfigureAwait(false);
                return;
            }

            if (eventArgs.Exception is MissingParameterValueException)
            {
                if (interactionService.IsAvailable && AddUnresolvedParameter(parameterResource))
                {
                    _ = EnsureParameterResolutionTaskRunningAsync();
                }

                await UpdateParameterStateAsync(parameterResource, eventArgs.Exception.Message, new(KnownResourceStates.ValueMissing, KnownResourceStateStyles.Warn)).ConfigureAwait(false);
                return;
            }

            OnParameterResolved(_unresolvedParameters, parameterResource);
            await UpdateParameterStateAsync(parameterResource, eventArgs.Exception.Message, new("Error initializing parameter", KnownResourceStateStyles.Error)).ConfigureAwait(false);
        }
        finally
        {
            updateLock.Release();
        }
    }

    private SemaphoreSlim GetParameterUpdateLock(ParameterResource parameterResource)
    {
        lock (_observedParametersLock)
        {
            if (!_parameterUpdateLocks.TryGetValue(parameterResource, out var updateLock))
            {
                updateLock = new SemaphoreSlim(1, 1);
                _parameterUpdateLocks.Add(parameterResource, updateLock);
            }

            return updateLock;
        }
    }

    private void AddSetParameterCommand(ParameterResource parameterResource)
    {
        var valueInput = parameterResource.CreateInput(
            SetParameterValueName,
            required: true,
            dynamicLoading: new InputLoadOptions
            {
                AlwaysLoadOnStart = true,
                LoadCallback = context =>
                {
                    if (context.Input.Value is not null)
                    {
                        return Task.CompletedTask;
                    }

                    if (parameterResource.TryGetCurrentValue(out var existingValue))
                    {
                        context.Input.Value = existingValue;
                    }

                    return Task.CompletedTask;
                }
            });

        var saveInput = new InteractionInput
        {
            Name = SaveToUserSecretsName,
            InputType = InputType.Boolean,
            Label = InteractionStrings.ParametersInputsRememberLabel,
            Description = !userSecretsManager.IsAvailable
                ? InteractionStrings.ParametersInputsRememberDescriptionNotConfigured
                : InteractionStrings.ParametersInputsRememberDescriptionConfigured,
            EnableDescriptionMarkdown = true,
            Disabled = !userSecretsManager.IsAvailable,
            DynamicLoading = new InputLoadOptions
            {
                AlwaysLoadOnStart = true,
                DependsOnInputs = [SetParameterValueName],
                LoadCallback = async context =>
                {
                    if (context.Input.Value is null)
                    {
                        var parameterSection = await deploymentStateManager.AcquireSectionAsync(parameterResource.ConfigurationKey, context.CancellationToken).ConfigureAwait(false);
                        if (parameterSection.Data.Count > 0)
                        {
                            context.Input.Value = "true";
                        }
                    }
                }
            }
        };

        parameterResource.Annotations.Add(new ResourceCommandAnnotation(
            name: KnownResourceCommands.SetParameterCommand,
            displayName: CommandStrings.SetParameterName,
            executeCommand: context => SetParameterCoreAsync(parameterResource, context.Arguments, context.CancellationToken),
            updateState: _ => ResourceCommandState.Enabled,
            displayDescription: CommandStrings.SetParameterDescription,
            arguments: [valueInput, saveInput],
            confirmationMessage: null,
            iconName: "Key",
            iconVariant: IconVariant.Regular,
            isHighlighted: true));

        var deleteFromSecretsInput = new InteractionInput
        {
            Name = DeleteFromUserSecretsName,
            InputType = InputType.Boolean,
            Label = InteractionStrings.ParametersInputsDeleteLabel,
            Description = InteractionStrings.ParametersInputsDeleteDescription,
            EnableDescriptionMarkdown = true,
            Disabled = !userSecretsManager.IsAvailable,
            DynamicLoading = new InputLoadOptions
            {
                AlwaysLoadOnStart = true,
                LoadCallback = async context =>
                {
                    if (!userSecretsManager.IsAvailable)
                    {
                        context.Input.Disabled = true;
                        return;
                    }
                    var parameterSection = await deploymentStateManager.AcquireSectionAsync(parameterResource.ConfigurationKey, context.CancellationToken).ConfigureAwait(false);
                    context.Input.Disabled = parameterSection.Data.Count == 0;
                }
            }
        };

        parameterResource.Annotations.Add(new ResourceCommandAnnotation(
            name: KnownResourceCommands.DeleteParameterCommand,
            displayName: CommandStrings.DeleteParameterName,
            executeCommand: context => DeleteParameterCoreAsync(parameterResource, context.Arguments, context.CancellationToken),
            updateState: _ => HasParameterValue(parameterResource) ? ResourceCommandState.Enabled : ResourceCommandState.Hidden,
            displayDescription: CommandStrings.DeleteParameterDescription,
            arguments: [deleteFromSecretsInput],
            confirmationMessage: null,
            iconName: "Delete",
            iconVariant: IconVariant.Regular,
            isHighlighted: true));
    }

    private static bool HasParameterValue(ParameterResource parameterResource)
    {
        return parameterResource.TryGetCurrentValue(out _);
    }

    /// <summary>
    /// Prompts the user to set a value for a single parameter.
    /// </summary>
    /// <param name="parameterResource">The parameter resource to set the value for.</param>
    /// <param name="cancellationToken">The cancellation token to observe while waiting for the interaction to complete.</param>
    /// <returns>A task that completes when the user has set the value or canceled the interaction.</returns>
    public async Task SetParameterAsync(ParameterResource parameterResource, CancellationToken cancellationToken = default)
    {
        var input = parameterResource.CreateInput(SetParameterValueName);

        if (parameterResource.TryGetCurrentValue(out var existingValue))
        {
            input.Value = existingValue;
        }

        var parameterSection = await deploymentStateManager.AcquireSectionAsync(parameterResource.ConfigurationKey, cancellationToken).ConfigureAwait(false);
        var hasSavedState = parameterSection.Data.Count > 0 && input.Value is not null;
        var saveParameterInput = CreateSaveParameterInput(hasSavedState);

        var result = await interactionService.PromptInputsAsync(
            InteractionStrings.SetParameterTitle,
            InteractionStrings.SetParameterMessage,
            [input, saveParameterInput],
            new InputsDialogInteractionOptions
            {
                PrimaryButtonText = InteractionStrings.ParametersInputsPrimaryButtonText,
                ShowDismiss = true,
                EnableMessageMarkdown = true,
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Canceled)
        {
            await SetParameterCoreAsync(parameterResource, result.Data, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets the value for a parameter without prompting the user.
    /// </summary>
    /// <param name="parameterResource">The parameter resource to set the value for.</param>
    /// <param name="value">The value to use for the parameter.</param>
    /// <param name="saveToUserSecrets">
    /// A value indicating whether to persist the value to user secrets. This only takes effect in run mode.
    /// In publish mode it is ignored because parameter values are written to deployment state automatically
    /// once initialization completes (see <see cref="SaveParametersToDeploymentStateAsync"/>).
    /// </param>
    /// <param name="cancellationToken">The cancellation token to observe while updating state.</param>
    /// <returns>A task that completes when the value has been applied.</returns>
    public Task SetValueAsync(ParameterResource parameterResource, string value, bool saveToUserSecrets = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameterResource);
        ArgumentException.ThrowIfNullOrEmpty(value);

        return SetValueCoreAsync(parameterResource, value, saveToUserSecrets, ParameterValueSource.Programmatic, cancellationToken);
    }

    /// <summary>
    /// Prompts the user to delete a parameter value.
    /// </summary>
    /// <param name="parameterResource">The parameter resource to delete the value for.</param>
    /// <param name="cancellationToken">The cancellation token to observe while waiting for the interaction to complete.</param>
    /// <returns>A task that completes when the user has deleted the value or canceled the interaction.</returns>
    public async Task DeleteParameterAsync(ParameterResource parameterResource, CancellationToken cancellationToken = default)
    {
        var parameterSection = await deploymentStateManager.AcquireSectionAsync(parameterResource.ConfigurationKey, cancellationToken).ConfigureAwait(false);
        var hasSavedState = parameterSection.Data.Count > 0;
        var message = string.Format(CultureInfo.CurrentCulture, InteractionStrings.DeleteParameterMessage, parameterResource.Name);
        var deleteFromUserSecretsInput = new InteractionInput
        {
            Name = DeleteFromUserSecretsName,
            InputType = InputType.Boolean,
            Label = InteractionStrings.ParametersInputsDeleteLabel,
            Description = InteractionStrings.ParametersInputsDeleteDescription,
            EnableDescriptionMarkdown = true
        };
        var inputs = hasSavedState ? [deleteFromUserSecretsInput] : Array.Empty<InteractionInput>();

        var result = await interactionService.PromptInputsAsync(
            InteractionStrings.DeleteParameterTitle,
            message,
            inputs,
            new InputsDialogInteractionOptions
            {
                PrimaryButtonText = InteractionStrings.DeleteParameterPrimaryButtonText,
                ShowDismiss = true,
                EnableMessageMarkdown = true,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Canceled)
        {
            return;
        }

        await DeleteParameterCoreAsync(parameterResource, result.Data, cancellationToken).ConfigureAwait(false);
    }

    private InteractionInput CreateSaveParameterInput(bool hasExistingValue)
    {
        return new InteractionInput
        {
            Name = SaveToUserSecretsName,
            InputType = InputType.Boolean,
            Label = InteractionStrings.ParametersInputsRememberLabel,
            // Default to true if value already exists (was read from user secrets)
            Value = hasExistingValue ? "true" : null,
            Description = !userSecretsManager.IsAvailable
                ? InteractionStrings.ParametersInputsRememberDescriptionNotConfigured
                : InteractionStrings.ParametersInputsRememberDescriptionConfigured,
            EnableDescriptionMarkdown = true,
            Disabled = !userSecretsManager.IsAvailable
        };
    }

    internal async Task<ExecuteCommandResult> SetParameterCoreAsync(ParameterResource parameterResource, InteractionInputCollection arguments, CancellationToken cancellationToken)
    {
        var value = arguments.GetString(SetParameterValueName);
        if (string.IsNullOrEmpty(value))
        {
            return CommandResults.Success();
        }

        var shouldSave = arguments[SaveToUserSecretsName].Value is { Length: > 0 } sv &&
            bool.TryParse(sv, out var s) && s;

        await SetValueCoreAsync(parameterResource, value, shouldSave, ParameterValueSource.UserInteraction, cancellationToken).ConfigureAwait(false);

        return new ExecuteCommandResult { Success = true, Message = string.Format(CultureInfo.InvariantCulture, CommandStrings.ResourceSetParameter, parameterResource.Name) };
    }

    internal async Task<ExecuteCommandResult> DeleteParameterCoreAsync(ParameterResource parameterResource, InteractionInputCollection arguments, CancellationToken cancellationToken)
    {
        try
        {
            var deleteFromUserSecrets = arguments.TryGetByName(DeleteFromUserSecretsName, out var deleteFromUserSecretsInput) &&
                deleteFromUserSecretsInput.Value is { Length: > 0 } deleteInputValue &&
                bool.TryParse(deleteInputValue, out var shouldDelete) && shouldDelete;

            if (deleteFromUserSecrets)
            {
                var parameterSection = await deploymentStateManager.AcquireSectionAsync(parameterResource.ConfigurationKey, cancellationToken).ConfigureAwait(false);
                parameterSection.Data.Clear();
                await deploymentStateManager.DeleteSectionAsync(parameterSection, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Parameter value deleted from deployment state for {ParameterName}.", parameterResource.Name);

                loggerService.GetLogger(parameterResource)
                    .LogInformation("Parameter resource {ResourceName} value has been deleted from user secrets.", parameterResource.Name);
            }
            else
            {
                logger.LogInformation("Parameter value cleared for {ParameterName} (not deleted from user secrets).", parameterResource.Name);

                loggerService.GetLogger(parameterResource)
                    .LogInformation("Parameter resource {ResourceName} value has been cleared.", parameterResource.Name);
            }

            if (parameterResource.Required)
            {
                ObserveParameterValueChanges(parameterResource);
                await parameterResource.SetExceptionAsync(new MissingParameterValueException("Parameter value has been deleted."), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ObserveParameterValueChanges(parameterResource);
                await parameterResource.SetValueAsync(null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete parameter {ParameterName} from deployment state.", parameterResource.Name);
            return CommandResults.Failure($"Failed to delete parameter '{parameterResource.Name}'.");
        }

        return new ExecuteCommandResult { Success = true, Message = string.Format(CultureInfo.InvariantCulture, CommandStrings.ResourceDeletedParameter, parameterResource.Name) };
    }

    private async Task SetValueCoreAsync(ParameterResource parameterResource, string inputValue, bool saveToUserSecrets, ParameterValueSource valueSource, CancellationToken cancellationToken = default)
    {
        ObserveParameterValueChanges(parameterResource);
        await parameterResource.SetValueAsync(inputValue, cancellationToken).ConfigureAwait(false);

        var resolutionSource = valueSource == ParameterValueSource.UserInteraction ? "user interaction" : "code";
        loggerService.GetLogger(parameterResource)
            .LogInformation("Parameter resource {ResourceName} has been resolved via {ResolutionSource}.", parameterResource.Name, resolutionSource);

        // Persist to user secrets only when explicitly requested and running. In run mode the deployment
        // state manager is backed by user secrets; in publish mode it is file-backed and all parameter
        // values are saved together once initialization completes (see SaveParametersToDeploymentStateAsync),
        // so there is intentionally nothing to persist here for publish.
        if (executionContext.IsRunMode && saveToUserSecrets)
        {
            try
            {
                var slot = await deploymentStateManager.AcquireSectionAsync(parameterResource.ConfigurationKey, cancellationToken).ConfigureAwait(false);
                slot.SetValue(inputValue);
                await deploymentStateManager.SaveSectionAsync(slot, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Parameter value saved to deployment state for {ParameterName}.", parameterResource.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to save parameter {ParameterName} to deployment state.", parameterResource.Name);
            }
        }

        OnParameterResolved(_unresolvedParameters, parameterResource);
    }

    // Internal for testing purposes - allows passing specific parameters to test.
    internal async Task HandleUnresolvedParametersAsync(IList<ParameterResource> unresolvedParameters, CancellationToken cancellationToken)
    {
        var stateModified = false;

        // This method will continue in a loop until all unresolved parameters are resolved.
        while (HasUnresolvedParameters(unresolvedParameters))
        {
            var showNotification = executionContext.IsRunMode;
            var showSaveToSecrets = executionContext.IsRunMode;

            var proceedToInputs = true;

            if (showNotification)
            {
                // First we show a notification that there are unresolved parameters.
                var result = await interactionService.PromptNotificationAsync(
                    InteractionStrings.ParametersBarTitle,
                    InteractionStrings.ParametersBarMessage,
                    new NotificationInteractionOptions
                    {
                        Intent = MessageIntent.Warning,
                        PrimaryButtonText = InteractionStrings.ParametersBarPrimaryButtonText
                    },
                    cancellationToken).ConfigureAwait(false);

                proceedToInputs = result.Data;
            }

            if (proceedToInputs)
            {
                // Now we build up a new form base on the unresolved parameters.
                var resourceInputs = new List<(ParameterResource ParameterResource, InteractionInput Input)>();
                var unresolvedParametersSnapshot = GetUnresolvedParametersSnapshot(unresolvedParameters);

                foreach (var parameter in unresolvedParametersSnapshot)
                {
                    // Create an input for each unresolved parameter.
                    var input = parameter.CreateInput();
                    resourceInputs.Add((parameter, input));
                }

                var inputs = resourceInputs.Select(i => i.Input).ToList();
                InteractionInput? saveParameters = null;

                if (showSaveToSecrets)
                {
                    saveParameters = CreateSaveParameterInput(hasExistingValue: false);
                    inputs.Add(saveParameters);
                }

                var message = executionContext.IsPublishMode
                    ? InteractionStrings.ParametersInputsMessagePublishMode
                    : InteractionStrings.ParametersInputsMessage;

                var valuesPrompt = await interactionService.PromptInputsAsync(
                    InteractionStrings.ParametersInputsTitle,
                    message,
                    [.. inputs],
                    new InputsDialogInteractionOptions
                    {
                        PrimaryButtonText = InteractionStrings.ParametersInputsPrimaryButtonText,
                        ShowDismiss = true,
                        EnableMessageMarkdown = true,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!valuesPrompt.Canceled)
                {
                    var shouldSave = saveParameters?.Value is not null &&
                        bool.TryParse(saveParameters.Value, out var saveToDeploymentState) && saveToDeploymentState;

                    // Iterate through the unresolved parameters and set their values based on user input.
                    for (var i = resourceInputs.Count - 1; i >= 0; i--)
                    {
                        var (parameter, input) = (resourceInputs[i].ParameterResource, resourceInputs[i].Input);
                        var inputValue = input.Value;

                        if (string.IsNullOrEmpty(inputValue))
                        {
                            // If the input value is null, we skip this parameter.
                            continue;
                        }

                        await SetValueCoreAsync(parameter, inputValue, shouldSave, ParameterValueSource.UserInteraction, cancellationToken).ConfigureAwait(false);

                        if (shouldSave)
                        {
                            stateModified = true;
                        }

                        // Remove the parameter from unresolved parameters list.
                        OnParameterResolved(unresolvedParameters, parameter);
                    }
                }
            }
        }

        if (stateModified)
        {
            logger.LogInformation("Parameter values saved to deployment state.");
        }
    }

    private void OnParameterResolved(IList<ParameterResource> unresolvedParameters, ParameterResource parameter)
    {
        if (ReferenceEquals(unresolvedParameters, _unresolvedParameters))
        {
            lock (_unresolvedParametersLock)
            {
                unresolvedParameters.Remove(parameter);

                if (unresolvedParameters.Count == 0)
                {
                    _allParametersResolvedCts?.Cancel();
                }
            }

            return;
        }

        unresolvedParameters.Remove(parameter);

        if (unresolvedParameters.Count == 0)
        {
            _allParametersResolvedCts?.Cancel();
        }
    }

    private bool AddUnresolvedParameter(ParameterResource parameter)
    {
        lock (_unresolvedParametersLock)
        {
            if (_unresolvedParameters.Contains(parameter))
            {
                return false;
            }

            _unresolvedParameters.Add(parameter);
            return true;
        }
    }

    private bool HasUnresolvedParameters(IList<ParameterResource> unresolvedParameters)
    {
        if (ReferenceEquals(unresolvedParameters, _unresolvedParameters))
        {
            lock (_unresolvedParametersLock)
            {
                return unresolvedParameters.Count > 0;
            }
        }

        return unresolvedParameters.Count > 0;
    }

    private List<ParameterResource> GetUnresolvedParametersSnapshot(IList<ParameterResource> unresolvedParameters)
    {
        if (ReferenceEquals(unresolvedParameters, _unresolvedParameters))
        {
            lock (_unresolvedParametersLock)
            {
                return [.. unresolvedParameters];
            }
        }

        return [.. unresolvedParameters];
    }

    private enum ParameterValueSource
    {
        UserInteraction,
        Programmatic
    }

    private async Task SaveParametersToDeploymentStateAsync(IEnumerable<ParameterResource> parameters, CancellationToken cancellationToken)
    {
        var savedCount = 0;
        foreach (var parameter in parameters)
        {
            try
            {
                var value = await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(value))
                {
                    var slot = await deploymentStateManager.AcquireSectionAsync(parameter.ConfigurationKey, cancellationToken).ConfigureAwait(false);
                    slot.SetValue(value);
                    await deploymentStateManager.SaveSectionAsync(slot, cancellationToken).ConfigureAwait(false);
                    savedCount++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to save parameter {ParameterName} to deployment state.", parameter.Name);
            }
        }

        if (savedCount > 0)
        {
            logger.LogInformation("{SavedCount} parameter values saved to deployment state.", savedCount);
        }
    }

    private async Task UpdateParameterStateAsync(ParameterResource parameterResource, string? value, ResourceStateSnapshot? state)
    {
        await notificationService.PublishUpdateAsync(parameterResource, s =>
        {
            var properties = value is null
                ? s.Properties.RemoveResourceProperty(KnownProperties.Parameter.Value)
                : s.Properties.SetResourceProperty(KnownProperties.Parameter.Value, value, parameterResource.Secret);

            return s with
            {
                Properties = properties,
                State = state
            };
        }).ConfigureAwait(false);
    }
}
