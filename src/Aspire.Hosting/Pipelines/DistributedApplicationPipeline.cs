// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003

using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Pipelines;

[DebuggerDisplay("{ToString(),nq}")]
internal sealed class DistributedApplicationPipeline : IDistributedApplicationPipeline
{
    private readonly List<PipelineStep> _steps = [];
    private readonly List<Func<PipelineConfigurationContext, Task>> _configurationCallbacks = [];

    // Store resolved pipeline data for diagnostics
    private List<PipelineStep>? _lastResolvedSteps;

    public DistributedApplicationPipeline()
    {
        // Dependency order
        // {verb} -> {user steps} -> {verb}-prereq

        // Initialize with a "deploy" step that has a no-op callback
        _steps.Add(new PipelineStep
        {
            Name = WellKnownPipelineSteps.Deploy,
            Description = "Aggregation step for all deploy operations. All deploy steps should be required by this step.",
            Action = _ => Task.CompletedTask,
        });

        var parameterPromptingStep = new PipelineStep
        {
            Name = WellKnownPipelineSteps.ProcessParameters,
            Description = "Prompts for parameter values before build, publish, or deployment operations.",
            Action = async context =>
            {
                // Parameter processing - ensure all parameters are initialized and resolved
                var parameterProcessor = context.Services.GetRequiredService<ParameterProcessor>();
                await parameterProcessor.InitializeParametersAsync(context.Model, waitForResolution: true, context.CancellationToken).ConfigureAwait(false);
            }
        };
        parameterPromptingStep.RequiredBy(WellKnownPipelineSteps.DeployPrereq);
        parameterPromptingStep.RequiredBy(WellKnownPipelineSteps.BuildPrereq);
        parameterPromptingStep.RequiredBy(WellKnownPipelineSteps.PublishPrereq);
        _steps.Add(parameterPromptingStep);

        _steps.Add(new PipelineStep
        {
            Name = WellKnownPipelineSteps.DeployPrereq,
            Description = "Prerequisite step that runs before any deploy operations. Initializes deployment environment and manages deployment state.",
            Action = async context =>
            {
                // REVIEW: Break this up into smaller steps

                var hostEnvironment = context.Services.GetRequiredService<IHostEnvironment>();
                var options = context.Services.GetRequiredService<IOptions<PipelineOptions>>();

                context.Logger.LogInformation("Initializing deployment for environment '{EnvironmentName}'", hostEnvironment.EnvironmentName);
                var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();

                if (deploymentStateManager.StateFilePath is string stateFilePath && File.Exists(stateFilePath))
                {
                    // Check if --clear-cache flag is set and prompt user before deleting deployment state
                    if (!options.Value.ClearCache)
                    {
                        // Add a task to show the deployment state file path if available
                        context.Logger.LogInformation("Deployment state will be loaded from: {StateFilePath}", stateFilePath);
                    }
                    else
                    {
                        var interactionService = context.Services.GetRequiredService<IInteractionService>();
                        if (interactionService.IsAvailable)
                        {
                            var result = await interactionService.PromptNotificationAsync(
                                "Clear Deployment State",
                                $"The deployment state for the '{hostEnvironment.EnvironmentName}' environment will be deleted. Do you want to continue?",
                                new NotificationInteractionOptions
                                {
                                    Intent = MessageIntent.Confirmation,
                                    ShowSecondaryButton = true,
                                    ShowDismiss = false,
                                    PrimaryButtonText = "Yes",
                                    SecondaryButtonText = "No"
                                },
                                context.CancellationToken).ConfigureAwait(false);

                            if (result.Canceled || !result.Data)
                            {
                                // User declined or canceled - exit the deployment
                                context.Logger.LogInformation("User declined to clear deployment state. Canceling pipeline execution.");

                                throw new OperationCanceledException("Pipeline execution canceled by user.");
                            }

                            // User confirmed - delete the deployment state file
                            context.Logger.LogInformation("Deleting deployment state file at {Path} due to --clear-cache flag", stateFilePath);
                            File.Delete(stateFilePath);
                        }
                    }
                }

                var computeResources = context.Model.Resources
                        .Where(r => r.RequiresImageBuild())
                        .ToList();

                var uniqueDeployTag = $"aspire-deploy-{DateTime.UtcNow:yyyyMMddHHmmss}";

                context.Logger.LogInformation("Setting default deploy tag '{Tag}' for compute resource(s).", uniqueDeployTag);

                // Resources that were built, will get this tag unless they have a custom ContainerImagePushOptionsCallbackAnnotation
                foreach (var resource in context.Model.GetBuildResources())
                {
                    if (resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>().Any())
                    {
                        continue;
                    }

                    resource.Annotations.Add(new ContainerImagePushOptionsCallbackAnnotation(context =>
                    {
                        context.Options.RemoteImageTag = uniqueDeployTag;
                    }));
                }
            }
        });

        // Add a default "build" step
        _steps.Add(new PipelineStep
        {
            Name = WellKnownPipelineSteps.Build,
            Description = "Aggregation step for all build operations. All build steps should be required by this step.",
            Action = _ => Task.CompletedTask,
        });

        _steps.Add(new PipelineStep
        {
            Name = WellKnownPipelineSteps.BuildPrereq,
            Description = "Prerequisite step that runs before any build operations.",
            Action = context => Task.CompletedTask
        });

        // Add a default "Push" meta-step that all push steps should be required by
        // Push unconditionally depends on PushPrereq to ensure annotations are set up
        var pushStep = new PipelineStep
        {
            Name = WellKnownPipelineSteps.Push,
            Description = "Aggregation step for all push operations. All push steps should be required by this step.",
            Action = _ => Task.CompletedTask
        };
        pushStep.DependsOn(WellKnownPipelineSteps.PushPrereq);
        _steps.Add(pushStep);

        _steps.Add(new PipelineStep
        {
            Name = WellKnownPipelineSteps.PushPrereq,
            Description = "Prerequisite step that runs before any push operations.",
            Action = async context =>
            {
                foreach (var resource in context.Model.Resources)
                {
                    if (!resource.RequiresImageBuildAndPush())
                    {
                        continue;
                    }

                    // Check if the resource is configured to save images as archives
                    // rather than pushing to a registry. These don't require a registry.
                    var buildOptionsContext = await resource.ProcessContainerBuildOptionsCallbackAsync(
                        context.Services,
                        context.Logger,
                        context.ExecutionContext,
                        context.CancellationToken).ConfigureAwait(false);

                    // Skip registry validation if Destination is explicitly set to Archive
                    if (buildOptionsContext.Destination == ContainerImageDestination.Archive)
                    {
                        // Ensure OutputPath is set when Destination is Archive
                        if (string.IsNullOrEmpty(buildOptionsContext.OutputPath))
                        {
                            throw new InvalidOperationException(
                                $"Resource '{resource.Name}' has Destination set to Archive but OutputPath is not configured. " +
                                $"Please set the OutputPath in the container build options.");
                        }
                        continue;
                    }

                    // Skip if resource already has a ContainerRegistryReferenceAnnotation (explicit WithContainerRegistry call)
                    if (resource.TryGetAnnotationsIncludingAncestorsOfType<ContainerRegistryReferenceAnnotation>(out var annotations) &&
                        annotations.Any())
                    {
                        continue;
                    }

                    // Skip if resource has a deployment target with a ContainerRegistry set
                    var deploymentTargetAnnotation = resource.GetDeploymentTargetAnnotation();
                    if (deploymentTargetAnnotation?.ContainerRegistry is not null)
                    {
                        continue;
                    }

                    // Check for RegistryTargetAnnotations (automatically added via BeforeStartEvent)
                    var registryTargetAnnotations = resource.Annotations.OfType<RegistryTargetAnnotation>().ToArray();

                    // When multiple registries exist, require explicit WithContainerRegistry call
                    if (registryTargetAnnotations.Length > 1)
                    {
                        var registryNames = string.Join(", ", registryTargetAnnotations.Select(a => a.Registry is IResource res ? res.Name : a.Registry.ToString()));
                        throw new InvalidOperationException(
                            $"Resource '{resource.Name}' requires image push but has multiple container registries available - '{registryNames}'. " +
                            $"Please specify which registry to use with '.WithContainerRegistry(registryBuilder)'.");
                    }

                    // When no registry is available, throw an error
                    if (registryTargetAnnotations.Length == 0)
                    {
                        throw new InvalidOperationException(
                            $"Resource '{resource.Name}' requires image push but no container registry is available. " +
                            $"Please add a container registry using 'builder.AddContainerRegistry(...)' or specify one with '.WithContainerRegistry(registryBuilder)'.");
                    }
                }
            },
        });

        // Add a default "Publish" aggregation step that all publish steps should be required by
        _steps.Add(new PipelineStep
        {
            Name = WellKnownPipelineSteps.Publish,
            Description = "Aggregation step for all publish operations. All publish steps should be required by this step.",
            Action = _ => Task.CompletedTask
        });

        _steps.Add(new PipelineStep
        {
            Name = WellKnownPipelineSteps.PublishPrereq,
            Description = "Prerequisite step that runs before any publish operations.",
            Action = _ => Task.CompletedTask,
        });

        // Add diagnostic step for dependency graph analysis
        _steps.Add(new PipelineStep
        {
            Name = WellKnownPipelineSteps.Diagnostics,
            Description = "Dumps dependency graph information for troubleshooting pipeline execution.",
            Action = async context =>
            {
                // Use the resolved pipeline data from the last ExecuteAsync call
                var stepsToAnalyze = _lastResolvedSteps ?? throw new InvalidOperationException(
                    "No resolved pipeline data available for diagnostics. Ensure that the pipeline has been executed before running diagnostics.");

                // Generate the diagnostic output using the resolved data
                DumpDependencyGraphDiagnostics(stepsToAnalyze, context);
            }
        });
    }

    public bool HasSteps => _steps.Count > 0;

    public void AddStep(string name,
        Func<PipelineStepContext, Task> action,
        object? dependsOn = null,
        object? requiredBy = null)
    {
        if (_steps.Any(s => s.Name == name))
        {
            throw new InvalidOperationException(
                $"A step with the name '{name}' has already been added to the pipeline.");
        }

        var step = new PipelineStep
        {
            Name = name,
            Action = action
        };

        if (dependsOn != null)
        {
            AddDependencies(step, dependsOn);
        }

        if (requiredBy != null)
        {
            AddRequiredBy(step, requiredBy);
        }

        _steps.Add(step);
    }

    private static void AddDependencies(PipelineStep step, object dependsOn)
    {
        if (dependsOn is string stepName)
        {
            step.DependsOn(stepName);
        }
        else if (dependsOn is IEnumerable<string> stepNames)
        {
            foreach (var name in stepNames)
            {
                step.DependsOn(name);
            }
        }
        else
        {
            throw new ArgumentException(
                $"The dependsOn parameter must be a string or IEnumerable<string>, but was {dependsOn.GetType().Name}.",
                nameof(dependsOn));
        }
    }

    private static void AddRequiredBy(PipelineStep step, object requiredBy)
    {
        if (requiredBy is string stepName)
        {
            step.RequiredBy(stepName);
        }
        else if (requiredBy is IEnumerable<string> stepNames)
        {
            foreach (var name in stepNames)
            {
                step.RequiredBy(name);
            }
        }
        else
        {
            throw new ArgumentException(
                $"The requiredBy parameter must be a string or IEnumerable<string>, but was {requiredBy.GetType().Name}.",
                nameof(requiredBy));
        }
    }

    public void AddStep(PipelineStep step)
    {
        if (_steps.Any(s => s.Name == step.Name))
        {
            throw new InvalidOperationException(
                $"A step with the name '{step.Name}' has already been added to the pipeline.");
        }

        _steps.Add(step);
    }

    public void AddPipelineConfiguration(Func<PipelineConfigurationContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _configurationCallbacks.Add(callback);
    }

    public async Task ExecuteAsync(PipelineContext context)
    {
        var annotationSteps = await CollectStepsFromAnnotationsAsync(context).ConfigureAwait(false);
        var allSteps = _steps.Concat(annotationSteps).ToList();

        // Execute configuration callbacks even if there are no steps
        // This allows callbacks to run validation or other logic
        await ExecuteConfigurationCallbacksAsync(context, allSteps).ConfigureAwait(false);

        if (allSteps.Count == 0)
        {
            return;
        }

        ValidateSteps(allSteps);

        // Convert RequiredBy relationships to DependsOn relationships before filtering
        var allStepsByName = allSteps.ToDictionary(s => s.Name, StringComparer.Ordinal);
        NormalizeRequiredByToDependsOn(allSteps, allStepsByName);

        // Capture resolved pipeline data for diagnostics (before filtering)
        _lastResolvedSteps = allSteps;

        var (stepsToExecute, stepsByName) = FilterStepsForExecution(allSteps, context);

        // Build dependency graph and execute with readiness-based scheduler
        await ExecuteStepsAsTaskDag(stepsToExecute, stepsByName, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Converts all RequiredBy relationships to their equivalent DependsOn relationships.
    /// If step A is required by step B, this adds step A as a dependency of step B.
    /// </summary>
    private static void NormalizeRequiredByToDependsOn(
        List<PipelineStep> steps,
        Dictionary<string, PipelineStep> stepsByName)
    {
        foreach (var step in steps)
        {
            foreach (var requiredByStep in step.RequiredBySteps)
            {
                if (!stepsByName.TryGetValue(requiredByStep, out var requiredByStepObj))
                {
                    throw new InvalidOperationException(
                        $"Step '{step.Name}' is required by unknown step '{requiredByStep}'");
                }

                // Add the inverse relationship: if step A is required by step B,
                // then step B depends on step A
                if (!requiredByStepObj.DependsOnSteps.Contains(step.Name))
                {
                    requiredByStepObj.DependsOnSteps.Add(step.Name);
                }
            }
        }
    }

    private static (List<PipelineStep> StepsToExecute, Dictionary<string, PipelineStep> StepsByName) FilterStepsForExecution(
        List<PipelineStep> allSteps,
        PipelineContext context)
    {
        var pipelineOptions = context.Services.GetService<Microsoft.Extensions.Options.IOptions<PipelineOptions>>();
        var stepName = pipelineOptions?.Value.Step;
        var allStepsByName = allSteps.ToDictionary(s => s.Name, StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(stepName))
        {
            return (allSteps, allStepsByName);
        }

        if (!allStepsByName.TryGetValue(stepName, out var targetStep))
        {
            var availableSteps = string.Join(", ", allSteps.Select(s => $"'{s.Name}'"));
            throw new InvalidOperationException(
                $"Step '{stepName}' not found in pipeline. Available steps: {availableSteps}");
        }

        // Compute transitive dependencies of the target step (includes the target step itself)
        // Since RequiredBy relationships have been normalized to DependsOn,
        // this automatically includes all steps that the target depends on
        var stepsToExecute = ComputeTransitiveDependencies(targetStep, allStepsByName);

        var filteredStepsByName = stepsToExecute.ToDictionary(s => s.Name, StringComparer.Ordinal);
        return (stepsToExecute, filteredStepsByName);
    }

    private static List<PipelineStep> ComputeTransitiveDependencies(
        PipelineStep step,
        Dictionary<string, PipelineStep> stepsByName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PipelineStep>();

        void Visit(string stepName)
        {
            if (!visited.Add(stepName))
            {
                return;
            }

            if (!stepsByName.TryGetValue(stepName, out var currentStep))
            {
                return;
            }

            foreach (var dependency in currentStep.DependsOnSteps)
            {
                Visit(dependency);
            }

            result.Add(currentStep);
        }

        // Visit the target step itself (which will also visit all its dependencies)
        Visit(step.Name);

        return result;
    }

    private static async Task<List<PipelineStep>> CollectStepsFromAnnotationsAsync(PipelineContext context)
    {
        var steps = new List<PipelineStep>();

        foreach (var resource in context.Model.Resources)
        {
            var annotations = resource.Annotations
                .OfType<PipelineStepAnnotation>();

            foreach (var annotation in annotations)
            {
                var factoryContext = new PipelineStepFactoryContext
                {
                    PipelineContext = context,
                    Resource = resource
                };

                var annotationSteps = await annotation.CreateStepsAsync(factoryContext).ConfigureAwait(false);
                foreach (var step in annotationSteps)
                {
                    steps.Add(step);
                    step.Resource ??= resource;
                }
            }
        }

        return steps;
    }

    private async Task ExecuteConfigurationCallbacksAsync(
        PipelineContext pipelineContext,
        List<PipelineStep> allSteps)
    {
        // Collect callbacks from the pipeline itself
        var callbacks = new List<Func<PipelineConfigurationContext, Task>>();

        callbacks.AddRange(_configurationCallbacks);

        // Collect callbacks from resource annotations
        foreach (var resource in pipelineContext.Model.Resources)
        {
            var annotations = resource.Annotations.OfType<PipelineConfigurationAnnotation>();
            foreach (var annotation in annotations)
            {
                callbacks.Add(annotation.Callback);
            }
        }

        // Execute all callbacks
        if (callbacks.Count > 0)
        {
            var configContext = new PipelineConfigurationContext
            {
                Services = pipelineContext.Services,
                Steps = allSteps.AsReadOnly(),
                Model = pipelineContext.Model
            };

            foreach (var callback in callbacks)
            {
                await callback(configContext).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateSteps(IEnumerable<PipelineStep> steps)
    {
        var stepNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (!stepNames.Add(step.Name))
            {
                throw new InvalidOperationException(
                    $"Duplicate step name: '{step.Name}'");
            }
        }

        foreach (var step in steps)
        {
            foreach (var dependency in step.DependsOnSteps)
            {
                if (!stepNames.Contains(dependency))
                {
                    throw new InvalidOperationException(
                        $"Step '{step.Name}' depends on unknown step '{dependency}'");
                }
            }

            foreach (var requiredBy in step.RequiredBySteps)
            {
                if (!stepNames.Contains(requiredBy))
                {
                    throw new InvalidOperationException(
                        $"Step '{step.Name}' is required by unknown step '{requiredBy}'");
                }
            }
        }
    }

    /// <summary>
    /// Executes pipeline steps by building a Task DAG where each step waits on its dependencies.
    /// Uses CancellationToken to stop remaining work when any step fails.
    /// </summary>
    private static async Task ExecuteStepsAsTaskDag(
        List<PipelineStep> steps,
        Dictionary<string, PipelineStep> stepsByName,
        PipelineContext context)
    {
        // Validate no cycles exist in the dependency graph
        ValidateDependencyGraph(steps, stepsByName);

        // Create a linked CancellationTokenSource that will be cancelled when any step fails
        // or when the original context token is cancelled
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        // Store the original token and set the linked token on the context
        var originalToken = context.CancellationToken;
        context.CancellationToken = linkedCts.Token;

        try
        {
            // Create a TaskCompletionSource for each step
            var stepCompletions = new Dictionary<string, TaskCompletionSource>(steps.Count, StringComparer.Ordinal);
            foreach (var step in steps)
            {
                stepCompletions[step.Name] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            // Execute a step after its dependencies complete
            async Task ExecuteStepWithDependencies(PipelineStep step)
            {
                var stepTcs = stepCompletions[step.Name];

                // Wait for all dependencies to complete (will throw if any dependency failed)
                if (step.DependsOnSteps.Count > 0)
                {
                    try
                    {
                        var depTasks = step.DependsOnSteps
                            .Where(stepCompletions.ContainsKey)
                            .Select(depName => stepCompletions[depName].Task);
                        await Task.WhenAll(depTasks).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Find all dependencies that failed
                        var failedDeps = step.DependsOnSteps
                            .Where(depName => stepCompletions.ContainsKey(depName) && stepCompletions[depName].Task.IsFaulted)
                            .ToList();

                        var message = failedDeps.Count > 0
                            ? $"Step '{step.Name}' cannot run because {(failedDeps.Count == 1 ? "dependency" : "dependencies")} {string.Join(", ", failedDeps.Select(d => $"'{d}'"))} failed"
                            : $"Step '{step.Name}' cannot run because a dependency failed";

                        // Wrap the dependency failure with context about this step
                        var wrappedException = new InvalidOperationException(message, ex);
                        stepTcs.TrySetException(wrappedException);
                        return;
                    }
                }

                try
                {
                    var activityReporter = context.Services.GetRequiredService<IPipelineActivityReporter>();
                    var reportingStep = await activityReporter.CreateStepAsync(step.Name, context.CancellationToken).ConfigureAwait(false);

                    await using (reportingStep.ConfigureAwait(false))
                    {
                        var stepContext = new PipelineStepContext
                        {
                            PipelineContext = context,
                            ReportingStep = reportingStep
                        };

                        try
                        {
                            PipelineLoggerProvider.CurrentStep = reportingStep;

                            await ExecuteStepAsync(step, stepContext).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            stepContext.Logger.LogError(ex, "Step '{StepName}' failed.", step.Name);

                            // Report the failure to the activity reporter before disposing
                            await reportingStep.FailAsync(ex.Message).ConfigureAwait(false);
                            throw;
                        }
                        finally
                        {
                            PipelineLoggerProvider.CurrentStep = null;
                        }
                    }

                    stepTcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    // Execution failure - mark as failed, cancel all other work, and re-throw
                    stepTcs.TrySetException(ex);

                    // Cancel all remaining work
                    try
                    {
                        linkedCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Ignore cancellation errors
                    }

                    throw;
                }
            }

            // Start all steps (they'll wait on their dependencies internally)
            var allStepTasks = new Task[steps.Count];
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                allStepTasks[i] = Task.Run(() => ExecuteStepWithDependencies(step));
            }

            // Wait for all steps to complete (or fail)
            try
            {
                await Task.WhenAll(allStepTasks).ConfigureAwait(false);
            }
            catch
            {
                // Collect all failed steps and their names
                var failures = allStepTasks
                    .Where(t => t.IsFaulted)
                    .Select(t => t.Exception!)
                    .SelectMany(ae => ae.InnerExceptions)
                    .ToList();

                if (failures.Count > 1)
                {
                    // Match failures to steps to get their names
                    var failedStepNames = new List<string>();
                    for (var i = 0; i < allStepTasks.Length; i++)
                    {
                        if (allStepTasks[i].IsFaulted)
                        {
                            failedStepNames.Add(steps[i].Name);
                        }
                    }

                    var message = failedStepNames.Count > 0
                        ? $"Multiple pipeline steps failed: {string.Join(", ", failedStepNames.Distinct())}"
                        : "Multiple pipeline steps failed.";

                    throw new AggregateException(message, failures);
                }

                // Single failure - just rethrow
                throw;
            }
        }
        finally
        {
            // Restore the original token
            context.CancellationToken = originalToken;
        }
    }

    /// <summary>
    /// Represents the visitation state of a step during cycle detection.
    /// </summary>
    private enum VisitState
    {
        /// <summary>
        /// The step has not been visited yet.
        /// </summary>
        Unvisited,

        /// <summary>
        /// The step is currently being visited (on the current DFS path).
        /// </summary>
        Visiting,

        /// <summary>
        /// The step has been fully visited (all descendants explored).
        /// </summary>
        Visited
    }

    /// <summary>
    /// Validates that the pipeline steps form a directed acyclic graph (DAG) with no circular dependencies.
    /// </summary>
    /// <remarks>
    /// Uses depth-first search (DFS) to detect cycles. A cycle exists if we encounter a node that is
    /// currently being visited (in the Visiting state), meaning we've found a back edge in the graph.
    ///
    /// Example: A → B → C is valid (no cycle)
    /// Example: A → B → C → A is invalid (cycle detected)
    /// Example: A → B, A → C, B → D, C → D is valid (diamond dependency, no cycle)
    /// </remarks>
    private static void ValidateDependencyGraph(
        List<PipelineStep> steps,
        Dictionary<string, PipelineStep> stepsByName)
    {
        // Note: RequiredBy relationships have already been normalized to DependsOn
        // in NormalizeRequiredByToDependsOn, so we don't need to process them here

        var visitStates = new Dictionary<string, VisitState>(steps.Count, StringComparer.Ordinal);
        foreach (var step in steps)
        {
            visitStates[step.Name] = VisitState.Unvisited;
        }

        // DFS to detect cycles
        void DetectCycles(string stepName, Stack<string> path)
        {
            if (!visitStates.TryGetValue(stepName, out var state))
            {
                return;
            }

            if (state == VisitState.Visiting) // Currently visiting - cycle detected!
            {
                var cycle = path.Reverse().SkipWhile(s => s != stepName).Append(stepName);
                throw new InvalidOperationException(
                    $"Circular dependency detected in pipeline steps: {string.Join(" → ", cycle)}");
            }

            if (state == VisitState.Visited) // Already fully visited - no need to check again
            {
                return;
            }

            visitStates[stepName] = VisitState.Visiting;
            path.Push(stepName);

            if (stepsByName.TryGetValue(stepName, out var step))
            {
                foreach (var dependency in step.DependsOnSteps)
                {
                    DetectCycles(dependency, path);
                }
            }

            path.Pop();
            visitStates[stepName] = VisitState.Visited;
        }

        // Check each step for cycles
        var path = new Stack<string>();
        foreach (var step in steps)
        {
            if (visitStates[step.Name] == VisitState.Unvisited)
            {
                DetectCycles(step.Name, path);
            }
        }
    }

    private static async Task ExecuteStepAsync(PipelineStep step, PipelineStepContext stepContext)
    {
        try
        {
            await step.Action(stepContext).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var exceptionInfo = ExceptionDispatchInfo.Capture(ex);
            throw new InvalidOperationException(
                $"Step '{step.Name}' failed: {ex.Message}", exceptionInfo.SourceException);
        }
    }

    /// <summary>
    /// Dumps comprehensive diagnostic information about the dependency graph, including
    /// reasons why certain steps may not be executed.
    /// </summary>
    private static void DumpDependencyGraphDiagnostics(
        List<PipelineStep> allSteps,
        PipelineStepContext context)
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("PIPELINE DEPENDENCY GRAPH DIAGNOSTICS");
        sb.AppendLine("=====================================");
        sb.AppendLine();
        sb.AppendLine("This diagnostic output shows the complete pipeline dependency graph structure.");
        sb.AppendLine("Use this to understand step relationships and troubleshoot execution issues.");
        sb.AppendLine();

        // Summary statistics
        sb.AppendLine(CultureInfo.InvariantCulture, $"Total steps defined: {allSteps.Count}");
        sb.AppendLine();

        // Always show full pipeline analysis for diagnostics
        sb.AppendLine("Analysis for full pipeline execution (showing all steps and their relationships)");
        sb.AppendLine();

        var allStepsByName = allSteps.ToDictionary(s => s.Name, StringComparer.Ordinal);

        // Build execution order (topological sort)
        var executionOrder = GetTopologicalOrder(allSteps);

        sb.AppendLine("EXECUTION ORDER");
        sb.AppendLine("===============");
        sb.AppendLine("This shows the order in which steps would execute, respecting all dependencies.");
        sb.AppendLine("Steps with no dependencies run first, followed by steps that depend on them.");
        sb.AppendLine();
        for (var i = 0; i < executionOrder.Count; i++)
        {
            var step = executionOrder[i];
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i + 1,3}. {step.Name}");
        }
        sb.AppendLine();

        // Detailed step analysis
        sb.AppendLine("DETAILED STEP ANALYSIS");
        sb.AppendLine("======================");
        sb.AppendLine("Shows each step's dependencies, associated resources, tags, and descriptions.");
        sb.AppendLine("✓ = dependency exists, ? = dependency missing");
        sb.AppendLine();

        foreach (var step in allSteps.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Step: {step.Name}");

            // Show description if available
            if (!string.IsNullOrWhiteSpace(step.Description))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Description: {step.Description}");
            }

            // Show dependencies
            if (step.DependsOnSteps.Count > 0)
            {
                sb.Append("    Dependencies: ");
                var depStatuses = step.DependsOnSteps
                    .OrderBy(dep => dep, StringComparer.Ordinal)
                    .Select(dep =>
                    {
                        var depExists = allStepsByName.ContainsKey(dep);
                        var icon = depExists ? "✓" : "?";
                        var status = depExists ? "" : " [missing]";
                        return $"{icon} {dep}{status}";
                    });
                sb.AppendLine(string.Join(", ", depStatuses));
            }
            else
            {
                sb.AppendLine("    Dependencies: none");
            }

            // Show resource association if available
            if (step.Resource != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Resource: {step.Resource.Name} ({step.Resource.GetType().Name})");
            }

            // Show tags if any
            if (step.Tags.Count > 0)
            {
                var sortedTags = step.Tags.OrderBy(tag => tag, StringComparer.Ordinal);
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Tags: {string.Join(", ", sortedTags)}");
            }

            // Since we're showing full pipeline analysis, no steps are filtered out
            // All steps will be marked as "WILL EXECUTE" in this diagnostic view

            sb.AppendLine();
        }

        // Show potential issues
        sb.AppendLine("POTENTIAL ISSUES:");
        sb.AppendLine("Identifies problems in the pipeline configuration that could prevent execution.");
        sb.AppendLine("─────────────────");
        var hasIssues = false;

        // Check for missing dependencies
        foreach (var step in allSteps)
        {
            foreach (var dep in step.DependsOnSteps)
            {
                if (!allStepsByName.ContainsKey(dep))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"WARNING: Step '{step.Name}' depends on missing step '{dep}'");
                    hasIssues = true;
                }
            }
        }

        // Check for orphaned steps (no dependencies and not required by anything)
        var orphanedSteps = allSteps.Where(step =>
            step.DependsOnSteps.Count == 0 &&
            !allSteps.Any(other => other.DependsOnSteps.Contains(step.Name)))
            .OrderBy(step => step.Name, StringComparer.Ordinal)
            .ToList();

        if (orphanedSteps.Count > 0)
        {
            sb.AppendLine("INFO: Orphaned steps (no dependencies, not required by others):");
            foreach (var step in orphanedSteps)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"   - {step.Name}");
            }
            hasIssues = true;
        }

        if (!hasIssues)
        {
            sb.AppendLine("No issues detected");
        }

        // What-if execution simulation
        sb.AppendLine();
        sb.AppendLine("EXECUTION SIMULATION (\"What If\" Analysis):");
        sb.AppendLine("Shows what steps would run for each possible target step and in what order.");
        sb.AppendLine("Steps at the same level can run concurrently.");
        sb.AppendLine("─────────────────────────────────────────────────────────────────────────────");

        // Show execution simulation for each step as a potential target
        foreach (var targetStep in allSteps.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"If targeting '{targetStep.Name}':");

            // Debug: Show what dependencies this step has after normalization
            if (targetStep.DependsOnSteps.Count > 0)
            {
                var sortedDeps = targetStep.DependsOnSteps.OrderBy(dep => dep, StringComparer.Ordinal);
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Direct dependencies: {string.Join(", ", sortedDeps)}");
            }
            else
            {
                sb.AppendLine("  Direct dependencies: none");
            }

            // Compute what would execute for this target
            var stepsForTarget = ComputeTransitiveDependencies(targetStep, allStepsByName);
            var executionLevels = GetExecutionLevelsByStep(stepsForTarget, allStepsByName);

            if (stepsForTarget.Count == 0)
            {
                sb.AppendLine("  No steps would execute (isolated step with missing dependencies)");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"  Total steps: {stepsForTarget.Count}");

            // Group steps by execution level for concurrency visualization
            var stepsByLevel = executionLevels.GroupBy(kvp => kvp.Value)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Select(kvp => kvp.Key).OrderBy(s => s, StringComparer.Ordinal).ToList());

            sb.AppendLine("  Execution order:");

            foreach (var level in stepsByLevel.Keys.OrderBy(l => l))
            {
                var stepsAtLevel = stepsByLevel[level];

                if (stepsAtLevel.Count == 1)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    [{level}] {stepsAtLevel[0]}");
                }
                else
                {
                    var parallelSteps = string.Join(" | ", stepsAtLevel);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    [{level}] {parallelSteps} (parallel)");
                }
            }
            sb.AppendLine();
        }

        context.ReportingStep.Log(LogLevel.Information, sb.ToString(), enableMarkdown: false);
    }

    /// <summary>
    /// Gets all transitive dependencies for a step (recursive).
    /// </summary>
    private static HashSet<string> GetAllTransitiveDependencies(
        PipelineStep step,
        Dictionary<string, PipelineStep> stepsByName,
        HashSet<string> visited)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var depName in step.DependsOnSteps)
        {
            if (visited.Contains(depName))
            {
                continue; // Avoid infinite recursion
            }

            result.Add(depName);

            if (stepsByName.TryGetValue(depName, out var depStep))
            {
                visited.Add(depName);
                var transitiveDeps = GetAllTransitiveDependencies(depStep, stepsByName, visited);
                result.UnionWith(transitiveDeps);
                visited.Remove(depName);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the execution level (distance from root steps) for a step.
    /// </summary>
    private static int GetExecutionLevel(PipelineStep step, Dictionary<string, PipelineStep> stepsByName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return GetExecutionLevelRecursive(step, stepsByName, visited);
    }

    /// <summary>
    /// Gets the execution levels for all steps in a collection.
    /// </summary>
    private static Dictionary<string, int> GetExecutionLevelsByStep(
        List<PipelineStep> steps,
        Dictionary<string, PipelineStep> stepsByName)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            result[step.Name] = GetExecutionLevel(step, stepsByName);
        }

        return result;
    }

    private static int GetExecutionLevelRecursive(
        PipelineStep step,
        Dictionary<string, PipelineStep> stepsByName,
        HashSet<string> visited)
    {
        if (visited.Contains(step.Name))
        {
            return 0; // Circular reference, treat as level 0
        }

        if (step.DependsOnSteps.Count == 0)
        {
            return 0; // Root step
        }

        visited.Add(step.Name);

        var maxLevel = 0;
        foreach (var depName in step.DependsOnSteps)
        {
            if (stepsByName.TryGetValue(depName, out var depStep))
            {
                var depLevel = GetExecutionLevelRecursive(depStep, stepsByName, visited);
                maxLevel = Math.Max(maxLevel, depLevel + 1);
            }
        }

        visited.Remove(step.Name);
        return maxLevel;
    }

    /// <summary>
    /// Gets the topological order of steps for execution.
    /// </summary>
    private static List<PipelineStep> GetTopologicalOrder(List<PipelineStep> steps)
    {
        var stepsByName = steps.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PipelineStep>();

        void Visit(PipelineStep step)
        {
            if (!visited.Add(step.Name))
            {
                return;
            }

            // Visit dependencies in sorted order for deterministic output
            var sortedDeps = step.DependsOnSteps.OrderBy(dep => dep, StringComparer.Ordinal);
            foreach (var depName in sortedDeps)
            {
                if (stepsByName.TryGetValue(depName, out var depStep))
                {
                    Visit(depStep);
                }
            }

            result.Add(step);
        }

        // Process steps in sorted order for deterministic output
        var sortedSteps = steps.OrderBy(s => s.Name, StringComparer.Ordinal);
        foreach (var step in sortedSteps)
        {
            if (!visited.Contains(step.Name))
            {
                Visit(step);
            }
        }

        return result;
    }

    public override string ToString()
    {
        if (_steps.Count == 0)
        {
            return "Pipeline: (empty)";
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Pipeline with {_steps.Count} step(s):");

        foreach (var step in _steps)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  - {step.Name}");

            if (step.DependsOnSteps.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" [depends on: {string.Join(", ", step.DependsOnSteps)}]");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
