// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Coordinates preparation and creation of executable DCP resources.
/// </summary>
internal sealed class ExecutableCreator(
    DcpNameGenerator nameGenerator,
    DistributedApplicationModel model,
    DcpAppResourceStore appResources,
    ExecutableConfigurationResolver configurationResolver,
    IConfiguration configuration,
    DistributedApplicationOptions distributedApplicationOptions,
    ExecutableLaunchPolicy launchPolicy,
    ILogger<ExecutableCreator> logger) : IObjectCreator<Executable, EmptyCreationContext>
{
    private readonly DcpNameGenerator _nameGenerator = nameGenerator;
    private readonly DistributedApplicationModel _model = model;
    private readonly DcpAppResourceStore _appResources = appResources;
    private readonly ExecutableConfigurationResolver _configurationResolver = configurationResolver;
    private readonly IConfiguration _configuration = configuration;
    private readonly DistributedApplicationOptions _distributedApplicationOptions = distributedApplicationOptions;
    private readonly ExecutableLaunchPolicy _launchPolicy = launchPolicy;
    private readonly ILogger<ExecutableCreator> _logger = logger;

    public IEnumerable<RenderedModelResource<Executable>> PrepareObjects(CancellationToken cancellationToken)
    {
        PrepareProjectExecutables(cancellationToken);
        PreparePlainExecutables();

        return _appResources.Get().OfType<RenderedModelResource<Executable>>();
    }

    public bool IsReadyToCreate(
        RenderedModelResource<Executable> resource,
        EmptyCreationContext context) =>
        !DcpModelUtilities.ShouldDeferCreateForExplicitStart(
            resource.ModelResource,
            resource.DcpResource.Spec.Start);

    public async Task CreateObjectAsync(
        RenderedModelResource<Executable> renderedResource,
        EmptyCreationContext context,
        ILogger resourceLogger,
        IDcpObjectFactory factory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configuration = await _configurationResolver
            .ResolveAsync(renderedResource, resourceLogger, cancellationToken)
            .ConfigureAwait(false);
        if (configuration.Configuration.Exception is not null)
        {
            throw new FailedToApplyEnvironmentException(
                $"Failed to apply configuration to executable {renderedResource.ModelResource.Name}",
                configuration.Configuration.Exception);
        }

        ExecutableLaunchPlan plan;
        try
        {
            plan = await ResolveLaunchPlanAsync(
                renderedResource.ModelResource,
                configuration.Configuration,
                _configuration,
                _distributedApplicationOptions,
                _launchPolicy,
                resourceLogger,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FailedToApplyEnvironmentException ex)
        {
            resourceLogger.LogError(ex, "{Message}", ex.Message);
            throw;
        }
        catch (ExecutableLaunchConfigurationException ex)
        {
            var failureMessage =
                $"Failed to apply launch configuration for resource '{renderedResource.ModelResource.Name}'. " +
                "Aspire does not retry launch configuration failures using DCP process fallback.";
            // DcpExecutor avoids duplicating FailedToApplyEnvironmentException logs, so record the underlying
            // launch producer failure on the resource logger before surfacing the actionable error.
            resourceLogger.LogError(ex, "{Message}", failureMessage);
            throw new FailedToApplyEnvironmentException(failureMessage, ex);
        }
        catch (Exception ex)
        {
            var failureMessage =
                $"Failed to create executable launch plan for resource '{renderedResource.ModelResource.Name}'. " +
                ex.Message;
            // Report launch-planning failures with their specific cause without misclassifying recipe or invariant
            // errors as IDE launch-configuration failures.
            resourceLogger.LogError(ex, "{Message}", failureMessage);
            throw new FailedToApplyEnvironmentException(failureMessage, ex);
        }

        Render(renderedResource, plan, configuration.PemCertificates, _logger);

        await factory
            .CreateDcpObjectsAsync([renderedResource.DcpResource], cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<ExecutableLaunchPlan> ResolveLaunchPlanAsync(
        IResource resource,
        IExecutionConfigurationResult executionConfiguration,
        IConfiguration configuration,
        DistributedApplicationOptions distributedApplicationOptions,
        ExecutableLaunchPolicy launchPolicy,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        var recipes = resource.Annotations.OfType<ExecutableLaunchRecipeAnnotation>().ToArray();
        if (recipes.Length != 1)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' must have exactly one executable launch recipe, but {recipes.Length} were found.");
        }

        var decision = launchPolicy.Decide(resource);
        var context = new ExecutableLaunchContext(
            resource,
            configuration,
            distributedApplicationOptions,
            executionConfiguration,
            decision,
            resourceLogger,
            cancellationToken);
        var plan = await recipes[0].Recipe.CreateLaunchPlanAsync(context).ConfigureAwait(false);

        if (plan.Mechanism != decision.Mechanism)
        {
            throw new InvalidOperationException(
                $"The executable launch recipe for resource '{resource.Name}' returned a {plan.Mechanism} plan after {decision.Mechanism} was selected.");
        }

        if (plan.Mechanism == ExecutableLaunchMechanism.Ide && plan.LaunchConfigurations.Count == 0)
        {
            throw new InvalidOperationException(
                $"The executable launch recipe for resource '{resource.Name}' selected IDE execution without producing a launch configuration.");
        }

        return plan;
    }

    internal static void Render(
        RenderedModelResource<Executable> renderedResource,
        ExecutableLaunchPlan plan,
        ExecutablePemCertificates? pemCertificates,
        ILogger logger)
    {
        var executable = renderedResource.DcpResource;
        var spec = executable.Spec;

        // Executable objects are reused on restart. Apply every launch field from the completed immutable plan so
        // a failed prior attempt cannot leak stale execution type, arguments, environment, or launch metadata.
        spec.ExecutablePath = plan.Command;
        spec.WorkingDirectory = plan.WorkingDirectory;
        spec.ExecutionType = plan.Mechanism switch
        {
            ExecutableLaunchMechanism.Process => ExecutionType.Process,
            ExecutableLaunchMechanism.Ide => ExecutionType.IDE,
            _ => throw new InvalidOperationException($"Unknown executable launch mechanism '{plan.Mechanism}'.")
        };
        spec.FallbackExecutionTypes = null;
        spec.Args = plan.Arguments?.ToList();
        spec.Env = plan.EnvironmentVariables
            .Select(static variable => new EnvVar { Name = variable.Key, Value = variable.Value })
            .ToList();
        spec.PemCertificates = pemCertificates;

        executable.Metadata.Annotations?.Remove(Executable.LaunchConfigurationsAnnotation);
        if (plan.LaunchConfigurations.Count > 0)
        {
            executable.SetAnnotationAsObjectList(Executable.LaunchConfigurationsAnnotation, plan.LaunchConfigurations);
        }

        executable.SetAnnotationAsObjectList(
            CustomResource.ResourceAppArgsAnnotation,
            plan.DisplayArguments.Select(static argument => new AppLaunchArgumentAnnotation(
                argument.Value,
                argument.IsSensitive,
                argument.EffectiveArgumentIndex)));
        // Hidden launch-tool arguments are intentionally absent from resource.appArgs, but launch metadata
        // still needs their sensitivity without copying their resolved values into another DCP annotation.
        executable.SetAnnotationAsObjectList(
            Executable.SensitiveEffectiveArgumentIndexesAnnotation,
            plan.LaunchArguments
                .Where(static argument => argument.IsSensitive && argument.EffectiveArgumentIndex is not null)
                .Select(static argument => argument.EffectiveArgumentIndex!.Value));

        ApplyLifetime(renderedResource.ModelResource, spec);
        ApplyTerminal(renderedResource.ModelResource, executable, logger);
    }

    private void PrepareProjectExecutables(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var project in _model.GetProjectResources())
        {
            if (!project.TryGetProjectMetadata(out var projectMetadata))
            {
                throw new InvalidOperationException($"Project resource '{project.Name}' is missing required metadata.");
            }

            EnsureRequiredAnnotations(project);
            var replicas = project.GetReplicaCount();

            for (var i = 0; i < replicas; i++)
            {
                var instance = DcpExecutor.GetDcpInstance(project, instanceIndex: i);
                project.TryGetLastAnnotation<ExecutableAnnotation>(out var executableAnnotation);
                var executable = Executable.Create(instance.Name, executableAnnotation?.Command ?? "dotnet");
                executable.Spec.WorkingDirectory =
                    executableAnnotation?.WorkingDirectory ??
                    Path.GetDirectoryName(projectMetadata.ProjectPath);

                ApplyCommonAnnotations(executable, project, instance, replicas, i);
                ApplyExplicitStart(project, executable.Spec);
                DcpExecutor.SetInitialResourceState(project, executable);
                AddRenderedResource(project, executable);
            }
        }
    }

    private void PreparePlainExecutables()
    {
        foreach (var resource in _model.GetExecutableResources())
        {
            EnsureRequiredAnnotations(resource);

            var instance = DcpExecutor.GetDcpInstance(resource, instanceIndex: 0);
            var executable = Executable.Create(instance.Name, resource.Command);
            executable.Spec.WorkingDirectory = resource.WorkingDirectory;

            ApplyCommonAnnotations(executable, resource, instance, replicaCount: 1, replicaIndex: 0);
            ApplyExplicitStart(resource, executable.Spec);
            DcpExecutor.SetInitialResourceState(resource, executable);
            AddRenderedResource(resource, executable);
        }
    }

    private static void ApplyCommonAnnotations(
        Executable executable,
        IResource resource,
        DcpInstance instance,
        int replicaCount,
        int replicaIndex)
    {
        executable.Annotate(CustomResource.OtelServiceNameAnnotation, resource.Name);
        executable.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, resource.GetOtelServiceInstanceId(instance));
        executable.Annotate(CustomResource.ResourceNameAnnotation, resource.Name);
        executable.Annotate(CustomResource.ResourceReplicaCount, replicaCount.ToString(CultureInfo.InvariantCulture));
        executable.Annotate(CustomResource.ResourceReplicaIndex, replicaIndex.ToString(CultureInfo.InvariantCulture));
    }

    private static void ApplyExplicitStart(IResource resource, ExecutableSpec spec)
    {
        if (resource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _))
        {
            spec.Start = false;
        }
    }

    private static void ApplyLifetime(IResource resource, ExecutableSpec spec)
    {
        spec.Persistent = null;
        spec.MonitorPid = null;
        spec.MonitorTimestamp = null;

        if (resource.GetLifetimeType() != Lifetime.Persistent)
        {
            return;
        }

        spec.Persistent = true;
        if (resource.TryGetParentProcessLifetime(out var parentProcessId, out var parentProcessTimestamp))
        {
            spec.MonitorPid = parentProcessId;
            spec.MonitorTimestamp = parentProcessTimestamp;
        }
    }

    private static void ApplyTerminal(IResource resource, Executable executable, ILogger logger)
    {
        executable.Spec.Terminal = null;
        if (!resource.TryGetAnnotationsOfType<TerminalAnnotation>(out var terminalAnnotations) ||
            terminalAnnotations.FirstOrDefault() is not { } terminalAnnotation)
        {
            return;
        }

        if (TryGetReplicaIndex(executable, out var replicaIndex) &&
            replicaIndex >= 0 &&
            replicaIndex < terminalAnnotation.TerminalHosts.Count)
        {
            executable.Spec.Terminal = new TerminalSpec
            {
                UdsPath = terminalAnnotation.TerminalHosts[replicaIndex].Layout.ProducerUdsPath,
                // The Aspire terminal host owns the listener at UdsPath; DCP must dial it.
                SocketMode = "connect",
                Cols = terminalAnnotation.Options.Columns,
                Rows = terminalAnnotation.Options.Rows
            };
            return;
        }

        logger.LogWarning(
            "Could not determine a producer UDS path for replica of resource '{ResourceName}'; terminal will not be attached for this replica.",
            resource.Name);
    }

    private static bool TryGetReplicaIndex(Executable executable, out int replicaIndex)
    {
        replicaIndex = -1;
        return executable.Metadata.Annotations is { } annotations &&
            annotations.TryGetValue(CustomResource.ResourceReplicaIndex, out var value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out replicaIndex);
    }

    private void EnsureRequiredAnnotations(IResource resource)
    {
        resource.AddLifeCycleCommands();
        _nameGenerator.EnsureDcpInstancesPopulated(resource);
    }

    private void AddRenderedResource(IResource resource, Executable executable)
    {
        var renderedResource = new RenderedModelResource<Executable>(resource, executable);
        DcpModelUtilities.AddServicesProducedInfo(renderedResource, _appResources.Get());
        _appResources.Add(renderedResource);
    }
}
