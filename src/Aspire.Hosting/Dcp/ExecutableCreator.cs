// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIRECERTIFICATES001

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

using ExecutableConfiguration = (IExecutionConfigurationResult Configuration, ExecutablePemCertificates? PemCertificates);

/// <summary>
/// Handles preparation and creation of Executable DCP resources (project executables and plain executables).
/// </summary>
internal sealed class ExecutableCreator : IObjectCreator<Executable, EmptyCreationContext>
{
    private readonly IConfiguration _configuration;
    private readonly DcpNameGenerator _nameGenerator;
    private readonly DistributedApplicationModel _model;
    private readonly DistributedApplicationOptions _distributedApplicationOptions;
    private readonly DistributedApplicationExecutionContext _executionContext;
    private readonly Locations _locations;
    private readonly IAspireStore _aspireStore;
    private readonly ILogger<ExecutableCreator> _logger;
    private readonly DcpAppResourceStore _appResources;

    public ExecutableCreator(
        IConfiguration configuration,
        DcpNameGenerator nameGenerator,
        DistributedApplicationModel model,
        DistributedApplicationOptions distributedApplicationOptions,
        DistributedApplicationExecutionContext executionContext,
        Locations locations,
        IAspireStore aspireStore,
        ILogger<ExecutableCreator> logger,
        DcpAppResourceStore appResources)
    {
        _configuration = configuration;
        _nameGenerator = nameGenerator;
        _model = model;
        _distributedApplicationOptions = distributedApplicationOptions;
        _executionContext = executionContext;
        _locations = locations;
        _aspireStore = aspireStore;
        _logger = logger;
        _appResources = appResources;
    }

    public IEnumerable<RenderedModelResource<Executable>> PrepareObjects(CancellationToken cancellationToken)
    {
        PrepareProjectExecutables(cancellationToken);
        PreparePlainExecutables();

        return _appResources.Get().OfType<RenderedModelResource<Executable>>();
    }

    public bool IsReadyToCreate(RenderedModelResource<Executable> resource, EmptyCreationContext context)
    {
        return !DcpModelUtilities.ShouldDeferCreateForExplicitStart(resource.ModelResource, resource.DcpResource.Spec.Start);
    }

    public async Task CreateObjectAsync(RenderedModelResource<Executable> er, EmptyCreationContext context, ILogger resourceLogger, IDcpObjectFactory factory, CancellationToken cancellationToken)
    {
        if (er.DcpResource is not Executable exe)
        {
            throw new InvalidOperationException($"Expected an Executable resource, but got {er.DcpResourceKind} instead");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var spec = exe.Spec;

        // Don't create an args collection unless needed.  When args is null, a project run by the IDE will use the arguments provided by its launch profile.
        // https://github.com/microsoft/aspire/blob/main/docs/specs/IDE-execution.md#launch-profile-processing-project-launch-configuration
        spec.Args = null;

        // An executable can be restarted so args must be reset to an empty state.
        // After resetting, first apply any dotnet project related args, e.g. configuration, and then add args from the model resource.
        if (er.DcpResource.TryGetAnnotationAsObjectList<string>(CustomResource.ResourceProjectArgsAnnotation, out var projectArgs) && projectArgs.Count > 0)
        {
            spec.Args ??= [];
            spec.Args.AddRange(projectArgs);
        }

        var (configuration, pemCertificates) = await BuildExecutableConfiguration(er, resourceLogger, cancellationToken).ConfigureAwait(false);

        spec.PemCertificates = pemCertificates;

        if (configuration.Exception is not null)
        {
            throw new FailedToApplyEnvironmentException($"Failed to apply configuration to executable {er.ModelResource.Name}", configuration.Exception);
        }

        // The launch configuration is applied before the command line is composed because applying it can switch the
        // execution type to Process, and the composition depends on the final execution type: an IDE-launched
        // resource does not receive the launch tool arguments its launch configuration already performs, a
        // process-launched one does.
        var launchToolArgumentsData = configuration.AdditionalConfigurationData.OfType<LaunchToolArgumentsData>().FirstOrDefault();
        var resolvedLaunchToolArgumentCount = launchToolArgumentsData?.Count ?? 0;
        var hasPreparedProjectArguments = spec.Args is { Count: > 0 };
        await ApplyLaunchConfigurationAsync(
            er,
            exe,
            configuration.EnvironmentVariables,
            resolvedLaunchToolArgumentCount,
            hasPreparedProjectArguments,
            cancellationToken).ConfigureAwait(false);
        ApplyResolvedProjectArguments(er, exe, resolvedLaunchToolArgumentCount);

        var omittedLaunchToolArgumentCount = OmitLaunchToolArguments(er, spec)
            ? resolvedLaunchToolArgumentCount
            : 0;

        var executableArgumentStartIndex = spec.Args?.Count ?? 0;
        var (launchArgs, dotnetProjectLaunchArgumentIndex, canReuseArgsForProcessFallback) = BuildLaunchArgs(
            er,
            spec,
            configuration.Arguments,
            executableArgumentStartIndex,
            resolvedLaunchToolArgumentCount,
            omittedLaunchToolArgumentCount,
            launchToolArgumentsData?.ShowInCommandLine ?? true);
        if (resolvedLaunchToolArgumentCount > 0 || !HasProjectLaunchArgsOverride(er.ModelResource))
        {
            AddDotnetProjectLaunchArgsForExecutableAnnotatedProject(launchArgs, dotnetProjectLaunchArgumentIndex, executableArgumentStartIndex);
        }
        var executableArgs = launchArgs.Where(a => a.Executable).Select(a => a.Value).ToList();
        var displayArgs = launchArgs.Where(a => a.Display).ToList();
        if (executableArgs.Count > 0)
        {
            spec.Args ??= [];
            spec.Args.AddRange(executableArgs);
        }
        // Arg annotations are what is displayed in the dashboard.
        er.DcpResource.SetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation, displayArgs.Select(a => new AppLaunchArgumentAnnotation(a.Value, isSensitive: a.IsSensitive, effectiveArgumentIndex: a.EffectiveArgumentIndex)));

        // Argument and launch-configuration callbacks can change on restart. Derive fallback availability from the
        // final execution type and resolved command line every time instead of carrying a preparation-time guess.
        spec.FallbackExecutionTypes = ShouldOfferProcessFallback(er.ModelResource, spec, resolvedLaunchToolArgumentCount, omittedLaunchToolArgumentCount, hasPreparedProjectArguments, canReuseArgsForProcessFallback)
            ? [ExecutionType.Process]
            : null;

        spec.Env = configuration.EnvironmentVariables.Select(kvp => new EnvVar { Name = kvp.Key, Value = kvp.Value }).ToList();

        // Configure the per-replica terminal spec if the resource has a TerminalAnnotation.
        // Each replica gets its own DCP UDS producer endpoint from the layout so the
        // terminal host can multiplex viewers per (resource, replica).
        //
        // PTY allocation is implemented by DCP across all three desktop platforms:
        //   * Windows  - ConPTY (the Win32 pseudo-console API; per-replica named pipe
        //                bridged into a Unix domain socket facade on the DCP side).
        //   * Linux    - Unix98 master/slave pair via /dev/ptmx + grantpt/unlockpt.
        //   * macOS    - Same Unix98 surface, with the Darwin posix_openpt path.
        // Container PTYs (interactive `docker exec`-style sessions) are not yet
        // wired through this annotation — tracked as a follow-up. If the running
        // DCP build pre-dates terminal allocation on this host (e.g. an older
        // bundled DCP that ships with Aspire), the executable fails to start
        // with termpty.ErrTerminalNotSupported surfaced through the reconciler.
        if (er.ModelResource.TryGetAnnotationsOfType<TerminalAnnotation>(out var terminalAnnotations))
        {
            var terminalAnnotation = terminalAnnotations.FirstOrDefault();
            if (terminalAnnotation is not null)
            {
                if (TryGetReplicaIndex(exe, out var replicaIndex)
                    && replicaIndex >= 0
                    && replicaIndex < terminalAnnotation.TerminalHosts.Count)
                {
                    spec.Terminal = new TerminalSpec
                    {
                        UdsPath = terminalAnnotation.TerminalHosts[replicaIndex].Layout.ProducerUdsPath,
                        // The Aspire terminal host owns the listener at UdsPath; DCP must dial it.
                        SocketMode = "connect",
                        Cols = terminalAnnotation.Options.Columns,
                        Rows = terminalAnnotation.Options.Rows
                    };
                }
                else
                {
                    _logger.LogWarning(
                        "Could not determine a producer UDS path for replica of resource '{ResourceName}'; terminal will not be attached for this replica.",
                        er.ModelResource.Name);
                }
            }
        }

        await factory.CreateDcpObjectsAsync([exe], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the resource's debug launch configuration after its execution configuration has been resolved.
    /// </summary>
    /// <remarks>
    /// Delaying the producer until creation lets it reuse the exact resolved environment without evaluating
    /// resource callbacks again. It also ensures endpoint-backed environment values are available.
    /// </remarks>
    private async Task ApplyLaunchConfigurationAsync(
        RenderedModelResource<Executable> er,
        Executable exe,
        IEnumerable<KeyValuePair<string, string>> environmentVariables,
        int resolvedLaunchToolArgumentCount,
        bool hasPreparedProjectArguments,
        CancellationToken cancellationToken)
    {
        if (er.ModelResource.HasAnnotationOfType<ForceProcessExecutionAnnotation>()
            || !er.ModelResource.SupportsDebugging(_configuration, out var supportsDebuggingAnnotation))
        {
            return;
        }

        var isProjectLaunchConfiguration =
            supportsDebuggingAnnotation.LaunchConfigurationType is KnownLaunchConfigurationTypes.Project;
        var hasProjectLaunchArgsOverride = HasProjectLaunchArgsOverride(er.ModelResource);
        List<JsonElement>? projectLaunchConfigurationsToPreserve = null;

        if (hasProjectLaunchArgsOverride &&
            !isProjectLaunchConfiguration &&
            exe.TryGetAnnotationAsObjectList<JsonElement>(Executable.LaunchConfigurationsAnnotation, out var launchConfigurations))
        {
            // Project launch overrides execute as processes, but DCP still consumes the project launch
            // configuration as project metadata. Preserve those entries while replacing the custom
            // producer result on restart. The annotation is a JSON array such as:
            // [{"type":"project",...},{"type":"maui",...}]
            projectLaunchConfigurationsToPreserve = launchConfigurations
                .Where(static configuration =>
                    configuration.ValueKind == JsonValueKind.Object &&
                    configuration.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    type.GetString() is KnownLaunchConfigurationTypes.Project)
                .ToList();
        }

        // A project launch override already supplies the process invocation. A "project" producer would describe
        // a launch mode that cannot be used, while custom producers can still contribute process-mode metadata.
        if (hasProjectLaunchArgsOverride && isProjectLaunchConfiguration)
        {
            return;
        }

        if (isProjectLaunchConfiguration && !er.ModelResource.TryGetProjectMetadata(out _))
        {
            throw new FailedToApplyEnvironmentException(
                $"Resource '{er.ModelResource.Name}' declares \"project\" debug launch support (WithDebugSupport) but has no project metadata. " +
                $"The \"project\" launch configuration type is reserved for .NET project resources; use a resource that carries {nameof(IProjectMetadata)} or a different launch configuration type.");
        }

        // A previous producer failure can leave the reusable spec in Process mode. Restore IDE execution on
        // restart unless a project launch override intentionally keeps this resource in Process mode.
        if (!hasProjectLaunchArgsOverride)
        {
            exe.Spec.ExecutionType = ExecutionType.IDE;
        }

        var mode = isProjectLaunchConfiguration
            ? GetProjectLaunchConfigurationMode()
            : _configuration[KnownConfigNames.DebugSessionRunMode] ?? ExecutableLaunchMode.NoDebug;
        var callbackContext = new LaunchConfigurationCallbackContext(
            mode,
            er.ModelResource,
            environmentVariables.ToDictionary(
                static variable => variable.Key,
                static variable => variable.Value,
                StringComparer.Ordinal),
            cancellationToken);

        try
        {
            // Executable objects are reused for restarts, so replace the prior producer result.
            exe.Annotate(Executable.LaunchConfigurationsAnnotation, string.Empty);
            await supportsDebuggingAnnotation
                .LaunchConfigurationAnnotator(exe, callbackContext)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (isProjectLaunchConfiguration)
            {
                throw;
            }

            if (HasIncompleteProcessCommand(er.ModelResource, supportsDebuggingAnnotation, resolvedLaunchToolArgumentCount, hasPreparedProjectArguments))
            {
                // This project-backed executable suppressed its process scaffold because the custom launch
                // configuration performs the tool invocation. With no resolved prefix to replace it, Process
                // execution would run a bare tool command such as `dotnet <app-args>`.
                throw;
            }

            // The command line is composed after this point, so Process execution receives the full tool invocation.
            _logger.LogWarning(ex, "Failed to apply launch configuration for resource '{ResourceName}'. Falling back to process execution.", er.ModelResource.Name);
            exe.Spec.ExecutionType = ExecutionType.Process;
        }
        finally
        {
            if (projectLaunchConfigurationsToPreserve is { Count: > 0 })
            {
                var updatedLaunchConfigurations =
                    exe.TryGetAnnotationAsObjectList<JsonElement>(Executable.LaunchConfigurationsAnnotation, out var customLaunchConfigurations)
                        ? customLaunchConfigurations
                        : [];
                updatedLaunchConfigurations.InsertRange(0, projectLaunchConfigurationsToPreserve);
                exe.SetAnnotationAsObjectList(Executable.LaunchConfigurationsAnnotation, updatedLaunchConfigurations);
            }
        }
    }

    /// <summary>
    /// Determines whether the resource's launch tool arguments must be omitted from the DCP executable spec because
    /// the IDE launch configuration performs that tool invocation itself.
    /// </summary>
    private bool OmitLaunchToolArguments(RenderedModelResource<Executable> er, ExecutableSpec spec)
    {
        if (spec.ExecutionType != ExecutionType.IDE)
        {
            return false;
        }

        // Only withhold when the launch configuration that claimed the tool invocation is the one actually in use.
        // A launch configuration of a different type knows nothing about it, so in that case the resource keeps its
        // full command line.
        return er.ModelResource.SupportsDebugging(_configuration, out var activeAnnotation)
            && er.ModelResource.HasLaunchToolArgsOwnedBy(activeAnnotation);
    }

    /// <summary>
    /// Determines whether DCP may fall back to Process execution when the IDE cannot launch the resource.
    /// </summary>
    /// <remarks>
    /// A Process fallback runs the DCP Executable spec's command and args "as is", so it is only meaningful when
    /// those args form a runnable command. A DCP Executable spec has a single <c>args</c> field, so it cannot carry
    /// both the IDE form and the process form of the command line: when the tool-invocation prefix is omitted for the
    /// IDE, no fallback can be offered.
    /// </remarks>
    private bool ShouldOfferProcessFallback(
        IResource modelResource,
        ExecutableSpec spec,
        int resolvedLaunchToolArgumentCount,
        int omittedLaunchToolArgumentCount,
        bool hasPreparedProjectArguments,
        bool canReuseArgsForProcessFallback)
    {
        if (spec.ExecutionType != ExecutionType.IDE ||
            omittedLaunchToolArgumentCount > 0 ||
            !canReuseArgsForProcessFallback)
        {
            return false;
        }

        var supportsDebugging = modelResource.SupportsDebugging(_configuration, out var annotation);

        // SupportsDebugging can return false while still yielding the resource's annotation, such as when Visual
        // Studio omits DEBUG_SESSION_INFO for a custom launch type. Check command completeness before the unsupported
        // path offers every ProjectResource a Process fallback.
        if (annotation is not null &&
            HasIncompleteProcessCommand(modelResource, annotation, resolvedLaunchToolArgumentCount, hasPreparedProjectArguments))
        {
            return false;
        }

        if (!supportsDebugging || annotation is null)
        {
            return modelResource is ProjectResource;
        }

        return modelResource is ProjectResource
            || annotation.LaunchConfigurationType is not KnownLaunchConfigurationTypes.Project;
    }

    private static bool HasIncompleteProcessCommand(
        IResource modelResource,
        SupportsDebuggingAnnotation annotation,
        int resolvedLaunchToolArgumentCount,
        bool hasPreparedProjectArguments)
    {
        // A custom project launcher such as Azure Functions owns the invocation when the integration has not
        // supplied an explicit executable. Ordinary WithArgs values are application arguments, so they cannot turn
        // the default `dotnet` executable into a runnable Process fallback.
        var customProjectLaunchOwnsInvocation =
            modelResource is ProjectResource &&
            annotation.LaunchConfigurationType is not KnownLaunchConfigurationTypes.Project &&
            !modelResource.HasAnnotationOfType<ExecutableAnnotation>();

        return resolvedLaunchToolArgumentCount == 0
            && !hasPreparedProjectArguments
            && modelResource.HasAnnotationOfType<IProjectMetadata>()
            && (modelResource.HasLaunchToolArgsOwnedBy(annotation) || customProjectLaunchOwnsInvocation);
    }

    private void PrepareProjectExecutables(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modelProjectResources = _model.GetProjectResources();

        foreach (var project in modelProjectResources)
        {
            if (!project.TryGetProjectMetadata(out var projectMetadata))
            {
                throw new InvalidOperationException($"Project resource '{project.Name}' is missing required metadata."); // Should never happen.
            }

            EnsureRequiredAnnotations(project);

            var replicas = project.GetReplicaCount();

            for (var i = 0; i < replicas; i++)
            {
                var exeInstance = DcpExecutor.GetDcpInstance(project, instanceIndex: i);
                project.TryGetLastAnnotation<ExecutableAnnotation>(out var executableAnnotation);

                var exe = Executable.Create(exeInstance.Name, executableAnnotation?.Command ?? "dotnet");
                exe.Spec.WorkingDirectory = executableAnnotation?.WorkingDirectory ?? Path.GetDirectoryName(projectMetadata.ProjectPath);

                exe.Annotate(CustomResource.OtelServiceNameAnnotation, project.Name);
                exe.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, project.GetOtelServiceInstanceId(exeInstance));
                exe.Annotate(CustomResource.ResourceNameAnnotation, project.Name);
                exe.Annotate(CustomResource.ResourceReplicaCount, replicas.ToString(CultureInfo.InvariantCulture));
                exe.Annotate(CustomResource.ResourceReplicaIndex, i.ToString(CultureInfo.InvariantCulture));

                DcpExecutor.SetInitialResourceState(project, exe);

                var projectArgs = new List<string>();

                var isInDebugSession = !string.IsNullOrEmpty(_configuration[DcpExecutor.DebugSessionPortVar]);
                var persistent = project.GetLifetimeType() == Lifetime.Persistent;
#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                project.TryGetLastAnnotation<ProjectLaunchArgsOverrideAnnotation>(out var launchOverride);
#pragma warning restore ASPIREPROJECTS001
                exe.Spec.Persistent = persistent;
                if (persistent)
                {
                    ApplyMonitorProcess(project, exe.Spec);
                }

                if (launchOverride is not null)
                {
                    exe.Spec.ExecutionType = ExecutionType.Process;
                    launchOverride.Apply(projectArgs, projectMetadata.ProjectPath, _distributedApplicationOptions.Configuration);

                    exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, CreateProjectLaunchConfiguration(project, projectMetadata));

                    exe.SetAnnotationAsObjectList(CustomResource.ResourceProjectArgsAnnotation, projectArgs);

                    if (project.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _))
                    {
                        exe.Spec.Start = false;
                    }

                    var overrideExeAppResource = new RenderedModelResource<Executable>(project, exe);
                    DcpModelUtilities.AddServicesProducedInfo(overrideExeAppResource, _appResources.Get());
                    _appResources.Add(overrideExeAppResource);

                    continue;
                }

                SupportsDebuggingAnnotation? supportsDebuggingAnnotation = null;
                var forceProcessExecution = project.HasAnnotationOfType<ForceProcessExecutionAnnotation>();
                if (!persistent && !forceProcessExecution && project.SupportsDebugging(_configuration, out supportsDebuggingAnnotation))
                {
                    exe.Spec.ExecutionType = ExecutionType.IDE;

                    // The active launch configuration producer runs later in CreateObjectAsync, after the
                    // resource's arguments and environment variables have been resolved.

                    // Keep a candidate Process command so custom IDE launch configurations whose launch-tool
                    // callback resolves empty have a runnable fallback. This also preserves the existing fallback
                    // for file-based apps, which some IDEs reject. CreateExecutableAsync removes the candidate
                    // when the active launch configuration or a non-empty launch-tool prefix replaces it.
                    if (executableAnnotation is null &&
                        (projectMetadata.IsFileBasedApp ||
                         (supportsDebuggingAnnotation.LaunchConfigurationType is not KnownLaunchConfigurationTypes.Project &&
                          project.HasLaunchToolArgsOwnedBy(supportsDebuggingAnnotation))))
                    {
                        AddDefaultProjectProcessArgs(projectArgs, projectMetadata);
                    }
                }
                else if (!persistent && !forceProcessExecution && ShouldFallBackToIdeExecution(isInDebugSession, supportsDebuggingAnnotation, executableAnnotation))
                {
                    // Fall back to IDE execution with a standard ProjectLaunchConfiguration when:
                    // 1. No SupportsDebuggingAnnotation exists (e.g. AddResource-based ProjectResource
                    //    subclasses that don't call WithDebugSupport). These should get the same IDE
                    //    treatment that AddProject provides by default.
                    // 2. The annotation exists but the IDE did not send DEBUG_SESSION_INFO (Visual Studio
                    //    scenario). VS handles project-like resources natively, so non-"project" types
                    //    like "azure-functions" still need IDE execution with ProjectLaunchConfiguration.
                    //    Resources with explicit executable commands, such as MAUI platform resources,
                    //    must preserve their process launch args unless an IDE explicitly advertises
                    //    support for their custom launch type.
                    exe.Spec.ExecutionType = ExecutionType.IDE;

                    exe.SetProjectLaunchConfiguration(CreateProjectLaunchConfiguration(project, projectMetadata));

                    if (executableAnnotation is null && projectMetadata.IsFileBasedApp)
                    {
                        AddDefaultProjectProcessArgs(projectArgs, projectMetadata);
                    }
                }
                else
                {
                    exe.Spec.ExecutionType = ExecutionType.Process;

                    // Some ProjectResource subtypes, such as MAUI platform resources, intentionally
                    // provide their own executable command and SDK-shaped app host args. Do not prefix
                    // those args with Aspire's default `dotnet run --project ...` wrapper.
                    if (executableAnnotation is null)
                    {
                        var projectLaunchConfiguration = new ProjectLaunchConfiguration
                        {
                            ProjectPath = projectMetadata.ProjectPath
                        };

                        AddDefaultProjectProcessArgs(projectArgs, projectMetadata);

                        // We want this annotation even if we are not using IDE execution; see ToSnapshot() for details.
                        exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, projectLaunchConfiguration);
                    }
                }

                exe.SetAnnotationAsObjectList(CustomResource.ResourceProjectArgsAnnotation, projectArgs);

                if (project.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _))
                {
                    exe.Spec.Start = false;
                }

                var exeAppResource = new RenderedModelResource<Executable>(project, exe);
                DcpModelUtilities.AddServicesProducedInfo(exeAppResource, _appResources.Get());
                _appResources.Add(exeAppResource);
            }
        }
    }

    private static void ApplyResolvedProjectArguments(RenderedModelResource<Executable> er, Executable exe, int resolvedLaunchToolArgumentCount)
    {
        if (er.ModelResource is not ProjectResource ||
            !er.ModelResource.TryGetProjectMetadata(out var projectMetadata))
        {
            return;
        }

        var projectLaunchConfigurationOwnsInvocation =
            exe.Spec.ExecutionType == ExecutionType.IDE &&
            exe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigurations) &&
            launchConfigurations.Any(static configuration => configuration.Type is KnownLaunchConfigurationTypes.Project);

        // ResourceProjectArgsAnnotation carries a candidate Process command. Discard it before effective argument
        // indexes are assigned when a resolved launch-tool prefix replaces it, or when a normal project IDE launch
        // owns the invocation. File-based apps retain their Process command because IDEs can reject that launch type.
        if (resolvedLaunchToolArgumentCount > 0 ||
            (projectLaunchConfigurationOwnsInvocation &&
             !projectMetadata.IsFileBasedApp))
        {
            exe.Spec.Args = null;
        }
    }

    private void AddDefaultProjectProcessArgs(List<string> projectArgs, IProjectMetadata projectMetadata)
    {
        // `dotnet watch` does not work with file-based apps yet, so use `dotnet run` in that case.
        if (_configuration.GetBool("DOTNET_WATCH") is not true || projectMetadata.IsFileBasedApp)
        {
            projectArgs.Add("run");
            projectArgs.Add(projectMetadata.IsFileBasedApp ? "--file" : "--project");
            projectArgs.Add(projectMetadata.ProjectPath);
            if (projectMetadata.IsFileBasedApp)
            {
                projectArgs.Add("--no-cache");
            }
            if (projectMetadata.SuppressBuild)
            {
                projectArgs.Add("--no-build");
            }
        }
        else
        {
            projectArgs.AddRange([
                "watch",
                "--non-interactive",
                "--no-hot-reload",
                "--project",
                projectMetadata.ProjectPath
            ]);
        }

        if (!string.IsNullOrEmpty(_distributedApplicationOptions.Configuration))
        {
            projectArgs.AddRange(["--configuration", _distributedApplicationOptions.Configuration]);
        }

        // Suppress dotnet's launch-profile handling because the application model materializes those settings
        // and they must take precedence over the ambient values that `dotnet run` would otherwise apply.
        projectArgs.Add("--no-launch-profile");
    }

    private void PreparePlainExecutables()
    {
        var modelExecutableResources = _model.GetExecutableResources();

        foreach (var executable in modelExecutableResources)
        {
            EnsureRequiredAnnotations(executable);

            var exeInstance = DcpExecutor.GetDcpInstance(executable, instanceIndex: 0);
            var exePath = executable.Command;
            var exe = Executable.Create(exeInstance.Name, exePath);

            // The working directory is always relative to the app host project directory (if it exists).
            exe.Spec.WorkingDirectory = executable.WorkingDirectory;
            exe.Annotate(CustomResource.OtelServiceNameAnnotation, executable.Name);
            exe.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, executable.GetOtelServiceInstanceId(exeInstance));
            exe.Annotate(CustomResource.ResourceNameAnnotation, executable.Name);
            // Plain executables are always single-replica today, but the terminal wire-up
            // (and any other replica-aware downstream logic) needs both annotations to be
            // present. Without them WithTerminal() can't resolve the producer UDS for the
            // replica and silently falls back to a no-op.
            exe.Annotate(CustomResource.ResourceReplicaCount, "1");
            exe.Annotate(CustomResource.ResourceReplicaIndex, "0");

            var persistent = executable.GetLifetimeType() == Lifetime.Persistent;
            if (persistent)
            {
                exe.Spec.Persistent = true;
                ApplyMonitorProcess(executable, exe.Spec);
            }

            if (!persistent
                && !executable.HasAnnotationOfType<ForceProcessExecutionAnnotation>()
                && executable.SupportsDebugging(_configuration, out _))
            {
                // Just mark as IDE execution here - the actual launch configuration callback
                // will be invoked in CreateExecutableAsync after endpoints are allocated.
                exe.Spec.ExecutionType = ExecutionType.IDE;
            }
            else
            {
                exe.Spec.ExecutionType = ExecutionType.Process;
            }

            if (executable.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _))
            {
                exe.Spec.Start = false;
            }

            DcpExecutor.SetInitialResourceState(executable, exe);

            var exeAppResource = new RenderedModelResource<Executable>(executable, exe);
            DcpModelUtilities.AddServicesProducedInfo(exeAppResource, _appResources.Get());
            _appResources.Add(exeAppResource);
        }
    }

    private static void ApplyMonitorProcess(IResource resource, ExecutableSpec spec)
    {
        if (resource.TryGetParentProcessLifetime(out var parentProcessId, out var parentProcessTimestamp))
        {
            spec.MonitorPid = parentProcessId;
            spec.MonitorTimestamp = parentProcessTimestamp;
        }
    }

    private async Task<ExecutableConfiguration> BuildExecutableConfiguration(RenderedModelResource<Executable> er, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        var exe = (Executable)er.DcpResource;

        var certificatesRootDir = GetCertificatesRootDirectory(er, exe);
        var bundleOutputPath = Path.Join(certificatesRootDir, "cert.pem");
        var customBundleOutputPath = Path.Join(certificatesRootDir, "bundles");
        var certificatesOutputPath = Path.Join(certificatesRootDir, "certs");
        var baseServerAuthOutputPath = Path.Join(certificatesRootDir, "private");

        var configuration = await ExecutionConfigurationBuilder.Create(er.ModelResource)
            .WithArgumentsConfig()
            .WithEnvironmentVariablesConfig()
            .WithCertificateTrustConfig(scope =>
            {
                var dirs = new List<string> { certificatesOutputPath };
                if (scope == CertificateTrustScope.Append)
                {
                    var existingSslCertDir = Environment.GetEnvironmentVariable(CertificateTrustExecutionConfigurationGatherer.SslCertDirEnvironmentVariable);
                    if (existingSslCertDir is not null)
                    {
                        dirs.AddRange(existingSslCertDir.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        // Do not invoke the openssl CLI here. This fallback is only for dotnet-run AppHosts
                        // where Aspire CLI did not already materialize OpenSSL's default directory into
                        // SSL_CERT_DIR, so reuse the same well-known certificate directories used for containers.
                        dirs.AddRange(ContainerCertificatePathsAnnotation.DefaultCertificateDirectoriesPaths.Where(Directory.Exists));
                    }
                }

                return new()
                {
                    CertificateBundlePath = ReferenceExpression.Create($"{bundleOutputPath}"),
                    // Build the SSL_CERT_DIR value by combining the new certs directory with any existing directories.
                    CertificateDirectoriesPath = ReferenceExpression.Create($"{string.Join(Path.PathSeparator, dirs)}"),
                    RootCertificatesPath = certificatesRootDir,
                };
            })
            .WithHttpsCertificateConfig(cert => new()
            {
                CertificatePath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{cert.Thumbprint}.crt")}"),
                KeyPath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{cert.Thumbprint}.key")}"),
                CertificateWithKeyPath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{cert.Thumbprint}.pem")}"),
                PfxPath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{cert.Thumbprint}.pfx")}"),
            })
            .BuildAsync(_executionContext, resourceLogger, cancellationToken)
            .ConfigureAwait(false);

        // Add the certificates to the executable spec so they'll be placed in the DCP config
        ExecutablePemCertificates? pemCertificates = null;
        if (configuration.TryGetAdditionalData<CertificateTrustExecutionConfigurationData>(out var certificateTrustConfiguration)
            && certificateTrustConfiguration.Scope != CertificateTrustScope.None
            && certificateTrustConfiguration.Certificates.Count > 0)
        {
            pemCertificates = new ExecutablePemCertificates
            {
                Certificates = CertificateUtilities.BuildPemCertificateList(certificateTrustConfiguration.Certificates),
                ContinueOnError = true,
            };

            if (certificateTrustConfiguration.CustomBundlesFactories.Count > 0)
            {
                Directory.CreateDirectory(customBundleOutputPath);
            }

            foreach (var bundleFactory in certificateTrustConfiguration.CustomBundlesFactories)
            {
                var bundleId = bundleFactory.Key;
                var bundleBytes = await bundleFactory.Value(certificateTrustConfiguration.Certificates, cancellationToken).ConfigureAwait(false);

                File.WriteAllBytes(Path.Join(customBundleOutputPath, bundleId), bundleBytes);
            }
        }

        if (configuration.TryGetAdditionalData<HttpsCertificateExecutionConfigurationData>(out var tlsCertificateConfiguration))
        {
            var thumbprint = tlsCertificateConfiguration.Certificate.Thumbprint;
            var publicCertificatePem = tlsCertificateConfiguration.Certificate.ExportCertificatePem();
            (var keyPem, var pfxBytes) = await DeveloperCertificateService.GetKeyMaterialAsync(
                certificate: tlsCertificateConfiguration.Certificate,
                password: tlsCertificateConfiguration.Password,
                needKeyPem: tlsCertificateConfiguration.IsKeyPathReferenced || tlsCertificateConfiguration.IsCertificateWithKeyPathReferenced,
                needPfx: tlsCertificateConfiguration.IsPfxPathReferenced,
                cancellationToken
            ).ConfigureAwait(false);

            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(baseServerAuthOutputPath);
            }
            else
            {
                Directory.CreateDirectory(baseServerAuthOutputPath, UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead);
            }

            File.WriteAllText(Path.Join(baseServerAuthOutputPath, $"{thumbprint}.crt"), publicCertificatePem);

            if (keyPem is not null)
            {
                var keyBytes = Encoding.ASCII.GetBytes(keyPem);

                // Write each of the certificate, key, and PFX assets to the temp folder
                File.WriteAllBytes(Path.Join(baseServerAuthOutputPath, $"{thumbprint}.key"), keyBytes);
                if (tlsCertificateConfiguration.IsCertificateWithKeyPathReferenced)
                {
                    File.WriteAllText(Path.Join(baseServerAuthOutputPath, $"{thumbprint}.pem"), new([.. keyPem, '\n', .. publicCertificatePem]));
                }

                Array.Clear(keyPem, 0, keyPem.Length);
                Array.Clear(keyBytes, 0, keyBytes.Length);
            }

            if (pfxBytes is not null)
            {
                File.WriteAllBytes(Path.Join(baseServerAuthOutputPath, $"{thumbprint}.pfx"), pfxBytes);
                Array.Clear(pfxBytes, 0, pfxBytes.Length);
            }
        }

        return (configuration, pemCertificates);
    }

    private string GetCertificatesRootDirectory(RenderedModelResource<Executable> er, Executable exe)
    {
        if (er.ModelResource.GetLifetimeType() == Lifetime.Persistent)
        {
            return Path.Join(_aspireStore.BasePath, "dcp", "executables", exe.Metadata.Name, "certificates");
        }

        return Path.Join(_locations.DcpSessionDir, exe.Metadata.Name);
    }

    private static (List<LaunchArgument> LaunchArgs, int? DotnetProjectLaunchArgumentIndex, bool CanReuseArgsForProcessFallback) BuildLaunchArgs(
        RenderedModelResource<Executable> er,
        ExecutableSpec spec,
        IEnumerable<(string Value, bool IsSensitive)> appHostArgs,
        int executableArgumentStartIndex,
        int launchToolArgumentCount,
        int omittedLaunchToolArgumentCount,
        bool showLaunchToolArgsInCommandLine
    )
    {
        // Launch args is the final list of args that are displayed in the UI and possibly added to the executable spec.
        // They're built from app host resource model args and any args in the effective launch profile.
        // Follows behavior in the IDE execution spec when in IDE execution mode:
        // https://github.com/microsoft/aspire/blob/main/docs/specs/IDE-execution.md#project-launch-configuration-type-project
        var appHostArgList = appHostArgs.ToList();
#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var hasProjectLaunchArgsOverride = er.ModelResource.TryGetLastAnnotation<ProjectLaunchArgsOverrideAnnotation>(out var projectLaunchArgsOverride);
#pragma warning restore ASPIREPROJECTS001
        var useProjectLaunchArgsOverride = hasProjectLaunchArgsOverride && launchToolArgumentCount == 0;
        if (useProjectLaunchArgsOverride &&
            projectLaunchArgsOverride?.LeadingResourceArgumentToRemove is { } leadingResourceArgumentToRemove &&
            appHostArgList.Count > 0 &&
            string.Equals(appHostArgList[0].Value, leadingResourceArgumentToRemove, StringComparison.Ordinal))
        {
            // Some integrations keep an SDK-shaped verb in resource args for model consumers, but the
            // launch override can already represent that verb. Only remove it when the annotation opts in.
            appHostArgList.RemoveAt(0);
            launchToolArgumentCount = Math.Max(0, launchToolArgumentCount - 1);
            omittedLaunchToolArgumentCount = Math.Max(0, omittedLaunchToolArgumentCount - 1);
        }

        var dotnetProjectLaunchResourceArgumentIndex = FindExecutableAnnotatedDotnetProjectLaunchArgumentIndex(
            er.ModelResource,
            appHostArgList);
        var launchArgs = new List<LaunchArgument>();
        int? dotnetProjectLaunchArgumentIndex = null;
        var canReuseArgsForProcessFallback = true;
        var nextExecutableArgumentIndex = executableArgumentStartIndex;
        List<string>? projectLaunchProfileArgs = null;
        var includeProfileArgsInSpec = false;

        LaunchArgument CreateLaunchArgument(string value, bool isSensitive, bool executable, bool display)
        {
            var effectiveArgumentIndex = executable ? nextExecutableArgumentIndex++ : (int?)null;
            return new(value, isSensitive, executable, display, effectiveArgumentIndex);
        }

        // If the executable is a project then include any command line args from the launch profile.
        if (!useProjectLaunchArgsOverride && er.ModelResource is ProjectResource project)
        {
            var projectLaunchConfigurationHandlesLaunchProfile =
                spec.ExecutionType == ExecutionType.IDE &&
                er.DcpResource.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var projectLaunchConfigurations) &&
                projectLaunchConfigurations.Any(static configuration => configuration.Type is KnownLaunchConfigurationTypes.Project);
            var ordinaryAppHostArgumentCount = Math.Max(0, appHostArgList.Count - launchToolArgumentCount);

            // Args in the launch profile is used when:
            // 1. The project is run as an executable. Launch profile args are combined with app host supplied args.
            // 2. A custom IDE launch configuration cannot carry launch_profile, so DCP supplies those args.
            // 3. A project IDE launch has no ordinary app host args, so the profile args are displayed.
            if (spec.ExecutionType == ExecutionType.Process ||
                !projectLaunchConfigurationHandlesLaunchProfile ||
                ordinaryAppHostArgumentCount == 0)
            {
                includeProfileArgsInSpec =
                    spec.ExecutionType == ExecutionType.Process ||
                    !projectLaunchConfigurationHandlesLaunchProfile;

                projectLaunchProfileArgs = GetLaunchProfileArgs(project.GetEffectiveLaunchProfile()?.LaunchProfile);
                if (includeProfileArgsInSpec &&
                    projectLaunchProfileArgs.Count > 0 &&
                    spec.ExecutionType == ExecutionType.IDE &&
                    IsExecutableAnnotatedDotnetProject(er.ModelResource) &&
                    executableArgumentStartIndex == 0 &&
                    launchToolArgumentCount == 0 &&
                    dotnetProjectLaunchResourceArgumentIndex is null)
                {
                    // Custom IDE launches preserve launch-profile application args before ordinary resource args.
                    // For an explicit dotnet application command, that can produce:
                    //   dotnet --profile-arg value exec app.dll
                    // Process execution requires `exec app.dll` before application args. The executable spec has one
                    // args list, so retain the IDE order and disable fallback instead of parsing every dotnet form.
                    // See https://learn.microsoft.com/dotnet/core/tools/dotnet#options-for-running-an-application.
                    canReuseArgsForProcessFallback = false;
                }

                if (projectLaunchProfileArgs.Count > 0 &&
                    ordinaryAppHostArgumentCount > 0 &&
                    launchToolArgumentCount == 0 &&
                    HasDotnetApplicationArgumentBoundary())
                {
                    // A prepared project command or explicit `dotnet run`/`dotnet watch` invocation needs a
                    // double-dash before application arguments. Custom IDE launchers receive raw application
                    // arguments instead.
                    projectLaunchProfileArgs.Insert(0, "--");
                }
            }

            bool HasDotnetApplicationArgumentBoundary()
            {
                if (executableArgumentStartIndex > 0)
                {
                    return true;
                }

                return dotnetProjectLaunchResourceArgumentIndex is { } index && index >= omittedLaunchToolArgumentCount;
            }
        }
        // Project launch-profile arguments are application arguments. When a custom launch-tool declaration replaces
        // the implicit `dotnet run` scaffold, keep its prefix first and insert profile arguments before ordinary
        // app-host arguments. Without such a declaration, preserve the existing profile-before-app-host ordering.
        var projectLaunchProfileArgumentInsertIndex = launchToolArgumentCount > 0
            ? Math.Min(launchToolArgumentCount, appHostArgList.Count)
            : 0;

        // Launch tool arguments (the tool-invocation prefix such as `run ./cmd/api`) are the leading app-host args,
        // and the two decisions about them are independent:
        //
        // - Executable: withheld only when the active IDE launch configuration performs the tool invocation itself,
        //   because passing it on would run it twice.
        // - Display: withheld only when the declaration asked for it. A prefix that the IDE performs is deliberately
        //   still shown, because it is absent from the process's effective args and hiding it here too would leave
        //   the dashboard showing a bare `go` plus the program arguments — the same treatment project launch-profile
        //   args get above.
        for (var i = 0; i <= appHostArgList.Count; i++)
        {
            if (i == projectLaunchProfileArgumentInsertIndex && projectLaunchProfileArgs is not null)
            {
                launchArgs.AddRange(projectLaunchProfileArgs.Select(
                    a => CreateLaunchArgument(a, isSensitive: false, includeProfileArgsInSpec, display: true)));
            }

            if (i == appHostArgList.Count)
            {
                break;
            }

            var a = appHostArgList[i];
            var isLaunchToolArg = i < launchToolArgumentCount;
            var launchArgument = CreateLaunchArgument(
                a.Value,
                a.IsSensitive,
                executable: i >= omittedLaunchToolArgumentCount,
                display: showLaunchToolArgsInCommandLine || !isLaunchToolArg);
            if (dotnetProjectLaunchResourceArgumentIndex == i && launchArgument.Executable)
            {
                dotnetProjectLaunchArgumentIndex = launchArgs.Count;
            }
            launchArgs.Add(launchArgument);
        }

        return (launchArgs, dotnetProjectLaunchArgumentIndex, canReuseArgsForProcessFallback);
    }

    private static int? FindExecutableAnnotatedDotnetProjectLaunchArgumentIndex(
        IResource resource,
        IReadOnlyList<(string Value, bool IsSensitive)> appHostArgs)
    {
        if (!IsExecutableAnnotatedDotnetProject(resource))
        {
            return null;
        }

        // Recognize the project-launching SDK verb only immediately after the dotnet executable:
        //   dotnet run ...
        //   dotnet watch ...
        // Later values belong to another SDK command or the launched application, for example:
        //   dotnet tool run <command>
        //   dotnet exec app.dll watch
        // They must not be interpreted as the top-level project-launch verb.
        // See https://learn.microsoft.com/dotnet/core/tools/dotnet-run and
        // https://learn.microsoft.com/dotnet/core/tools/dotnet-watch.
        if (appHostArgs.Count > 0 && appHostArgs[0].Value is "run" or "watch")
        {
            return 0;
        }

        return null;
    }

    private static bool IsExecutableAnnotatedDotnetProject(IResource resource)
    {
        return resource is ProjectResource &&
            resource.TryGetLastAnnotation<ExecutableAnnotation>(out var executableAnnotation) &&
            string.Equals(Path.GetFileNameWithoutExtension(executableAnnotation.Command), "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private void AddDotnetProjectLaunchArgsForExecutableAnnotatedProject(List<LaunchArgument> launchArgs, int? dotnetProjectLaunchArgumentIndex, int executableArgumentStartIndex)
    {
        if (dotnetProjectLaunchArgumentIndex is not { } projectLaunchIndex)
        {
            return;
        }

        List<LaunchArgument>? launchProfileArgs = null;
        var firstExecutableArgumentIndex = launchArgs.FindIndex(static argument => argument.Executable);
        if (firstExecutableArgumentIndex >= 0 &&
            projectLaunchIndex > firstExecutableArgumentIndex &&
            string.Equals(launchArgs[firstExecutableArgumentIndex].Value, "--", StringComparison.Ordinal))
        {
            // Executable launch-profile args were composed before the caller-provided project launch command.
            // Preserve any non-executable launch-tool display prefix, then move the profile segment after
            // the project launch command so the SDK parses it as application arguments.
            var launchProfileArgumentCount = projectLaunchIndex - firstExecutableArgumentIndex;
            launchProfileArgs = launchArgs.GetRange(firstExecutableArgumentIndex, launchProfileArgumentCount);
            launchArgs.RemoveRange(firstExecutableArgumentIndex, launchProfileArgumentCount);
            projectLaunchIndex -= launchProfileArgumentCount;
        }

        var argsToInsert = new List<string>();
        if (!string.IsNullOrEmpty(_distributedApplicationOptions.Configuration) &&
            !ContainsDotnetProjectLaunchOption(launchArgs, "--configuration", "-c"))
        {
            argsToInsert.AddRange(["--configuration", _distributedApplicationOptions.Configuration]);
        }

        if (!ContainsDotnetProjectLaunchOption(launchArgs, "--no-launch-profile") &&
            !ContainsDotnetProjectLaunchOption(launchArgs, "--launch-profile"))
        {
            argsToInsert.Add("--no-launch-profile");
        }

        if (argsToInsert.Count == 0 && launchProfileArgs is null)
        {
            return;
        }

        // Some ProjectResource subtypes provide a `dotnet run` or `dotnet watch` command through resource args
        // instead of using Aspire's default project wrapper. Keep the SDK-shaped command, but
        // preserve the same AppHost configuration and launch-profile suppression that regular
        // process-launched project resources get.
        if (argsToInsert.Count > 0)
        {
            launchArgs.InsertRange(projectLaunchIndex + 1, argsToInsert.Select(argument => new LaunchArgument(argument, IsSensitive: false, Executable: true, Display: false, EffectiveArgumentIndex: null)));
        }

        if (launchProfileArgs is not null)
        {
            // Launch profile args were originally before the app host args, separated by `--`.
            // Once this path preserves the caller-provided project launch command, those args must
            // move after the inserted SDK options so the SDK parses them as application args.
            launchArgs.AddRange(launchProfileArgs);
        }

        ReindexExecutableLaunchArgs(launchArgs, executableArgumentStartIndex);
    }

    private static bool ContainsDotnetProjectLaunchOption(List<LaunchArgument> launchArgs, params string[] options)
    {
        var separatorIndex = launchArgs.FindIndex(argument => argument.Executable && string.Equals(argument.Value, "--", StringComparison.Ordinal));
        var endIndex = separatorIndex < 0 ? launchArgs.Count : separatorIndex;

        for (var i = 0; i < endIndex; i++)
        {
            var value = launchArgs[i].Value;
            if (options.Any(option => string.Equals(value, option, StringComparison.Ordinal) || value.StartsWith(option + "=", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasProjectLaunchArgsOverride(IResource resource)
    {
#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        return resource.TryGetLastAnnotation<ProjectLaunchArgsOverrideAnnotation>(out _);
#pragma warning restore ASPIREPROJECTS001
    }

    private static void ReindexExecutableLaunchArgs(List<LaunchArgument> launchArgs, int executableArgumentStartIndex)
    {
        var nextExecutableArgumentIndex = executableArgumentStartIndex;
        for (var i = 0; i < launchArgs.Count; i++)
        {
            var argument = launchArgs[i];
            launchArgs[i] = argument with
            {
                EffectiveArgumentIndex = argument.Executable ? nextExecutableArgumentIndex++ : null
            };
        }
    }

    /// <summary>
    /// Determines whether to fall back to IDE execution for a project resource that did not pass
    /// <see cref="DebugSupportExtensions.SupportsDebugging"/>.
    /// </summary>
    private bool ShouldFallBackToIdeExecution(bool isInDebugSession, SupportsDebuggingAnnotation? supportsDebuggingAnnotation, ExecutableAnnotation? executableAnnotation)
    {
        if (!isInDebugSession)
        {
            return false;
        }

        if (executableAnnotation is not null && supportsDebuggingAnnotation?.LaunchConfigurationType is not null and not KnownLaunchConfigurationTypes.Project)
        {
            return false;
        }

        if (supportsDebuggingAnnotation is not null && !string.IsNullOrEmpty(_configuration[KnownConfigNames.DebugSessionInfo]))
        {
            return false;
        }

        return true;
    }

    private ProjectLaunchConfiguration CreateProjectLaunchConfiguration(IResource project, IProjectMetadata projectMetadata)
    {
        return ProjectLaunchConfigurationFactory.Create(project, projectMetadata, GetProjectLaunchConfigurationMode());
    }

    private string GetProjectLaunchConfigurationMode()
    {
        return _configuration[KnownConfigNames.DebugSessionRunMode]
            ?? (Debugger.IsAttached ? ExecutableLaunchMode.Debug : ExecutableLaunchMode.NoDebug);
    }

    private static List<string> GetLaunchProfileArgs(LaunchProfile? launchProfile)
    {
        if (launchProfile is not null && !string.IsNullOrWhiteSpace(launchProfile.CommandLineArgs))
        {
            return CommandLineArgsParser.Parse(launchProfile.CommandLineArgs);
        }

        return [];
    }

    private void EnsureRequiredAnnotations(IResource resource)
    {
        resource.AddLifeCycleCommands();
        _nameGenerator.EnsureDcpInstancesPopulated(resource);
    }

    private sealed record LaunchArgument(string Value, bool IsSensitive, bool Executable, bool Display, int? EffectiveArgumentIndex);

    private static bool TryGetReplicaIndex(Executable exe, out int replicaIndex)
    {
        replicaIndex = -1;
        if (exe.Metadata.Annotations is not { } annotations)
        {
            return false;
        }

        if (!annotations.TryGetValue(CustomResource.ResourceReplicaIndex, out var value))
        {
            return false;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out replicaIndex);
    }
}
