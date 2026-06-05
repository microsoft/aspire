#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
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
    IDeploymentStateManager deploymentStateManager)
{
    private readonly List<ParameterResource> _unresolvedParameters = [];

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
            var parameterResolutionTask = Task.Run(async () =>
            {
                try
                {
                    await HandleUnresolvedParametersAsync().ConfigureAwait(false);
                    logger.LogDebug("All unresolved parameters have been handled successfully.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to handle unresolved parameters.");
                }
            });

            if (waitForResolution)
            {
                await parameterResolutionTask.ConfigureAwait(false);
            }
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
        var currentDependencySet = new HashSet<object?>();

        await CollectDependentParameterResourcesAsync(model, referencedParameters, currentDependencySet, cancellationToken).ConfigureAwait(false);

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

    private async Task CollectDependentParameterResourcesAsync(DistributedApplicationModel model, Dictionary<string, ParameterResource> referencedParameters, HashSet<object?> currentDependencySet, CancellationToken cancellationToken)
    {
        foreach (var resource in model.Resources)
        {
            if (resource.IsExcludedFromPublish())
            {
                continue;
            }

            await ProcessResourceDependenciesAsync(resource, executionContext, referencedParameters, currentDependencySet, cancellationToken).ConfigureAwait(false);
        }

    }

    private async Task ProcessResourceDependenciesAsync(IResource resource, DistributedApplicationExecutionContext executionContext, Dictionary<string, ParameterResource> referencedParameters, HashSet<object?> currentDependencySet, CancellationToken cancellationToken)
    {
        // Process the resource's execution configuration to find referenced parameters
        var executionConfgiuration = await ExecutionConfigurationBuilder.Create(resource)
            .WithArgumentsConfig()
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, logger, cancellationToken).ConfigureAwait(false);

        foreach (var reference in executionConfgiuration.References)
        {
            TryAddDependentParameters(reference, referencedParameters, currentDependencySet);
        }
    }

    private static void TryAddDependentParameters(object? value, Dictionary<string, ParameterResource> referencedParameters, HashSet<object?> currentDependencySet)
    {
        if (value is ParameterResource parameter)
        {
            referencedParameters.TryAdd(parameter.Name, parameter);
        }
        else if (value is IValueWithReferences objectWithReferences)
        {
            currentDependencySet.Add(value);
            foreach (var dependency in objectWithReferences.References)
            {
                if (!currentDependencySet.Contains(dependency))
                {
                    TryAddDependentParameters(dependency, referencedParameters, currentDependencySet);
                }
            }
            currentDependencySet.Remove(value);
        }
    }

    private async Task ProcessParameterAsync(ParameterResource parameterResource)
    {
        try
        {
            var value = parameterResource.ValueInternal ?? "";

            await notificationService.PublishUpdateAsync(parameterResource, s =>
            {
                return s with
                {
                    Properties = s.Properties.SetResourceProperty(KnownProperties.Parameter.Value, value, parameterResource.Secret),
                    State = KnownResourceStates.Running
                };
            })
            .ConfigureAwait(false);

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

            await notificationService.PublishUpdateAsync(parameterResource, s =>
            {
                return s with
                {
                    State = new(stateText, stateStyle),
                    Properties = s.Properties.SetResourceProperty(KnownProperties.Parameter.Value, ex.Message)
                };
            })
            .ConfigureAwait(false);
        }
    }

    // Internal for testing purposes.
    private async Task HandleUnresolvedParametersAsync()
    {
        await HandleUnresolvedParametersAsync(_unresolvedParameters).ConfigureAwait(false);
    }

    // Internal for testing purposes - allows passing specific parameters to test.
    internal async Task HandleUnresolvedParametersAsync(IList<ParameterResource> unresolvedParameters)
    {
        var stateModified = false;

        {

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
                         })
                        .ConfigureAwait(false);

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
                        saveParameters = new InteractionInput
                        {
                            Name = "RememberParameters",
                            InputType = InputType.Boolean,
                            Label = InteractionStrings.ParametersInputsRememberLabel
                        };
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
                        })
                        .ConfigureAwait(false);

                    if (!valuesPrompt.Canceled)
                    {
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

                            parameter.WaitForValueTcs?.TrySetResult(inputValue);

                            // Update the parameter resource state to active with the provided value.
                            await notificationService.PublishUpdateAsync(parameter, s =>
                            {
                                return s with
                                {
                                    Properties = s.Properties.SetResourceProperty(KnownProperties.Parameter.Value, inputValue, parameter.Secret),
                                    State = KnownResourceStates.Running
                                };
                            })
                            .ConfigureAwait(false);

                            // Log that the parameter has been resolved
                            loggerService.GetLogger(parameter)
                                .LogInformation("Parameter resource {ResourceName} has been resolved via user interaction.", parameter.Name);

                            if (executionContext.IsRunMode && showSaveToSecrets && saveParameters?.Value is not null)
                            {
                                var shouldSave = bool.TryParse(saveParameters.Value, out var saveToDeploymentState) && saveToDeploymentState;
                                if (shouldSave)
                                {
                                    try
                                    {
                                        var slot = await deploymentStateManager.AcquireSectionAsync(parameter.ConfigurationKey).ConfigureAwait(false);
                                        slot.SetValue(inputValue);
                                        await deploymentStateManager.SaveSectionAsync(slot).ConfigureAwait(false);
                                        stateModified = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.LogWarning(ex, "Failed to save parameter {ParameterName} to deployment state.", parameter.Name);
                                    }
                                }
                            }

                            // Remove the parameter from unresolved parameters list.
                            unresolvedParameters.RemoveAt(i);
                        }
                    }
                }
            }

            if (stateModified)
            {
                logger.LogInformation("Parameter values saved to deployment state.");
            }
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
}
