#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
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
[Experimental("ASPIREINTERACTION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
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

    private readonly List<ParameterResource> _unresolvedParameters = [];
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
            parameterResource.WaitForValueTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            await ProcessParameterAsync(parameterResource).ConfigureAwait(false);
        }

        // If interaction service is available, we can handle unresolved parameters.
        // This will allow the user to provide values for parameters that could not be initialized.
        if (interactionService.IsAvailable && _unresolvedParameters.Count > 0)
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
        if (executionContext.IsRunMode && interactionService.IsAvailable && !parameterResource.Annotations.OfType<ResourceCommandAnnotation>().Any(a => a.Name == KnownResourceCommands.SetParameterCommand))
        {
            AddSetParameterCommand(parameterResource);
        }

        try
        {
            var value = parameterResource.ValueInternal ?? "";

            await UpdateParameterStateAsync(parameterResource, value, KnownResourceStates.Running).ConfigureAwait(false);

            parameterResource.WaitForValueTcs?.TrySetResult(value);
        }
        catch (Exception ex)
        {
            // Missing parameter values throw a MissingParameterValueException.
            if (interactionService.IsAvailable && ex is MissingParameterValueException)
            {
                // If interaction service is available, we can prompt the user to provide a value.
                // Add the parameter to unresolved parameters list.
                _unresolvedParameters.Add(parameterResource);

                loggerService.GetLogger(parameterResource)
                    .LogWarning("Parameter resource {ResourceName} could not be initialized. Waiting for user input.", parameterResource.Name);
            }
            else
            {
                // If interaction service is not available, we log the error and set the state to error.
                parameterResource.WaitForValueTcs?.TrySetException(ex);

                loggerService.GetLogger(parameterResource)
                    .LogError(ex, "Failed to initialize parameter resource {ResourceName}.", parameterResource.Name);
            }

            var stateText = ex is MissingParameterValueException ?
                "Value missing" :
                "Error initializing parameter";

            // Use warning style for missing parameters to match the notification banner,
            // and error style for actual initialization errors.
            var stateStyle = ex is MissingParameterValueException ?
                KnownResourceStateStyles.Warn :
                KnownResourceStateStyles.Error;

            await UpdateParameterStateAsync(parameterResource, ex.Message, new(stateText, stateStyle)).ConfigureAwait(false);
        }
    }

    private void AddSetParameterCommand(ParameterResource parameterResource)
    {
        parameterResource.Annotations.Add(new ResourceCommandAnnotation(
            name: KnownResourceCommands.SetParameterCommand,
            displayName: CommandStrings.SetParameterName,
            executeCommand: async context =>
            {
                await SetParameterAsync(parameterResource, context.CancellationToken).ConfigureAwait(false);
                return new ExecuteCommandResult { Success = true, Message = string.Format(CultureInfo.InvariantCulture, CommandStrings.ResourceSetParameter, parameterResource.Name) };
            },
            updateState: _ => ResourceCommandState.Enabled,
            displayDescription: CommandStrings.SetParameterDescription,
            parameter: null,
            confirmationMessage: null,
            iconName: "Key",
            iconVariant: IconVariant.Regular,
            isHighlighted: true));

        parameterResource.Annotations.Add(new ResourceCommandAnnotation(
            name: KnownResourceCommands.DeleteParameterCommand,
            displayName: CommandStrings.DeleteParameterName,
            executeCommand: async context =>
            {
                await DeleteParameterAsync(parameterResource, context.CancellationToken).ConfigureAwait(false);
                return new ExecuteCommandResult { Success = true, Message = string.Format(CultureInfo.InvariantCulture, CommandStrings.ResourceDeletedParameter, parameterResource.Name) };
            },
            updateState: _ => HasParameterValue(parameterResource) ? ResourceCommandState.Enabled : ResourceCommandState.Hidden,
            displayDescription: CommandStrings.DeleteParameterDescription,
            parameter: null,
            confirmationMessage: null,
            iconName: "Delete",
            iconVariant: IconVariant.Regular,
            isHighlighted: true));
    }

    private static bool HasParameterValue(ParameterResource parameterResource)
    {
        try
        {
            var value = parameterResource.ValueInternal;
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Prompts the user to set a value for a single parameter.
    /// </summary>
    /// <param name="parameterResource">The parameter resource to set the value for.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the user has set the value or cancelled.</returns>
    public async Task SetParameterAsync(ParameterResource parameterResource, CancellationToken cancellationToken = default)
    {
        var input = parameterResource.CreateInput();

        // Pre-populate input with existing value if the parameter has one
        try
        {
            var existingValue = parameterResource.ValueInternal;
            if (!string.IsNullOrEmpty(existingValue))
            {
                input.Value = existingValue;
            }
        }
        catch (Exception)
        {
            // No existing value, leave input empty
        }

        var parameterSection = await deploymentStateManager.AcquireSectionAsync(parameterResource.ConfigurationKey, cancellationToken).ConfigureAwait(false);
        var hasSavedState = parameterSection.Data.Count > 0 && input.Value != null;

        var saveParameterInput = CreateSaveParameterInput(hasSavedState);

        var inputs = new List<InteractionInput> { input, saveParameterInput };

        var result = await interactionService.PromptInputsAsync(
            InteractionStrings.SetParameterTitle,
            InteractionStrings.SetParameterMessage,
            inputs,
            new InputsDialogInteractionOptions
            {
                PrimaryButtonText = InteractionStrings.ParametersInputsPrimaryButtonText,
                ShowDismiss = true,
                EnableMessageMarkdown = true,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Canceled)
        {
            return;
        }

        if (string.IsNullOrEmpty(input.Value))
        {
            return;
        }

        var inputValue = input.Value;
        var shouldSave = saveParameterInput?.Value is not null &&
            bool.TryParse(saveParameterInput.Value, out var saveToDeploymentState) && saveToDeploymentState;

        await ApplyParameterValueAsync(parameterResource, inputValue, shouldSave, cancellationToken).ConfigureAwait(false);

        // Remove the parameter from unresolved parameters list.
        OnParameterResolved(_unresolvedParameters, parameterResource);
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
                : null,
            EnableDescriptionMarkdown = true,
            Disabled = !userSecretsManager.IsAvailable
        };
    }

    /// <summary>
    /// Deletes a parameter value from the deployment state and marks it as unresolved.
    /// </summary>
    /// <param name="parameterResource">The parameter resource to delete the value for.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the value has been deleted.</returns>
    public async Task DeleteParameterAsync(ParameterResource parameterResource, CancellationToken cancellationToken = default)
    {
        try
        {
            var parameterSection = await deploymentStateManager.AcquireSectionAsync(parameterResource.ConfigurationKey, cancellationToken).ConfigureAwait(false);
            var hasSavedState = parameterSection.Data.Count > 0;

            // Show different message based on whether value is saved in user secrets
            var message = hasSavedState
                ? string.Format(CultureInfo.CurrentCulture, InteractionStrings.DeleteParameterMessageWithUserSecrets, parameterResource.Name)
                : string.Format(CultureInfo.CurrentCulture, InteractionStrings.DeleteParameterMessage, parameterResource.Name);

            var inputs = new List<InteractionInput>();
            InteractionInput? deleteFromUserSecretsInput = null;

            // Add checkbox to delete from user secrets if value is saved there
            if (hasSavedState)
            {
                deleteFromUserSecretsInput = new InteractionInput
                {
                    Name = DeleteFromUserSecretsName,
                    InputType = InputType.Boolean,
                    Label = InteractionStrings.ParametersInputsDeleteLabel
                };
                inputs.Add(deleteFromUserSecretsInput);
            }

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

            // Check if user wants to delete from user secrets
            var deleteFromUserSecrets = deleteFromUserSecretsInput?.Value is { Length: > 0 } deleteInputValue &&
                bool.TryParse(deleteInputValue, out var shouldDelete) && shouldDelete;

            if (deleteFromUserSecrets)
            {
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

            // Add the parameter back to unresolved parameters
            if (!_unresolvedParameters.Contains(parameterResource))
            {
                _unresolvedParameters.Add(parameterResource);

                // Reset the WaitForValueTcs so the parameter can be resolved again
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs.SetException(new MissingParameterValueException("Parameter value has been deleted."));
                parameterResource.WaitForValueTcs = tcs;

                // Update the parameter's state to show it's missing a value
                await UpdateParameterStateAsync(parameterResource, "Parameter value has been deleted", new("Value missing", KnownResourceStateStyles.Warn)).ConfigureAwait(false);

                // Start the resolution task if it's not running
                _ = EnsureParameterResolutionTaskRunningAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete parameter {ParameterName} from deployment state.", parameterResource.Name);
        }
    }

    private async Task ApplyParameterValueAsync(ParameterResource parameterResource, string inputValue, bool saveToDeploymentState, CancellationToken cancellationToken = default)
    {
        // Update the parameter resource with the new value.
        // The parameter could already have a value set so recreate TCS in that situation.
        if (parameterResource.WaitForValueTcs?.Task.IsCompleted ?? false)
        {
            parameterResource.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        parameterResource.WaitForValueTcs?.TrySetResult(inputValue);

        await UpdateParameterStateAsync(parameterResource, inputValue, KnownResourceStates.Running).ConfigureAwait(false);

        // Log that the parameter has been resolved
        loggerService.GetLogger(parameterResource)
            .LogInformation("Parameter resource {ResourceName} has been resolved via user interaction.", parameterResource.Name);

        // Save to deployment state if requested and in run mode
        if (executionContext.IsRunMode && saveToDeploymentState)
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
    }

    // Internal for testing purposes - allows passing specific parameters to test.
    internal async Task HandleUnresolvedParametersAsync(IList<ParameterResource> unresolvedParameters, CancellationToken cancellationToken)
    {
        var stateModified = false;

        // This method will continue in a loop until all unresolved parameters are resolved.
        while (unresolvedParameters.Count > 0)
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

                foreach (var parameter in unresolvedParameters)
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

                        await ApplyParameterValueAsync(parameter, inputValue, shouldSave, cancellationToken).ConfigureAwait(false);

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
        unresolvedParameters.Remove(parameter);

        if (unresolvedParameters.Count == 0)
        {
            _allParametersResolvedCts?.Cancel();
        }
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

    private async Task UpdateParameterStateAsync(ParameterResource parameterResource, string value, ResourceStateSnapshot? state)
    {
        await notificationService.PublishUpdateAsync(parameterResource, s =>
        {
            return s with
            {
                Properties = s.Properties.SetResourceProperty(KnownProperties.Parameter.Value, value, parameterResource.Secret),
                State = state
            };
        }).ConfigureAwait(false);
    }
}
