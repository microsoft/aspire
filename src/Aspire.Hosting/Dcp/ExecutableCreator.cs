// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREDOTNETTOOL

using System.Diagnostics;
using System.Globalization;
using System.Text;
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

    public IEnumerable<RenderedModelResource<Executable>> PrepareObjects()
    {
        PrepareProjectExecutables();
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

        var executableArgumentStartIndex = spec.Args?.Count ?? 0;
        var launchArgs = BuildLaunchArgs(er, spec, configuration.Arguments, executableArgumentStartIndex);
        AddDotnetRunArgsForExecutableAnnotatedProject(er, launchArgs, executableArgumentStartIndex);
        var executableArgs = launchArgs.Where(a => a.Executable).Select(a => a.Value).ToList();
        var displayArgs = launchArgs.Where(a => a.Display).ToList();
        if (executableArgs.Count > 0)
        {
            spec.Args ??= [];
            spec.Args.AddRange(executableArgs);
        }
        // Arg annotations are what is displayed in the dashboard.
        er.DcpResource.SetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation, displayArgs.Select(a => new AppLaunchArgumentAnnotation(a.Value, isSensitive: a.IsSensitive, effectiveArgumentIndex: a.EffectiveArgumentIndex)));

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

        if (configuration.Exception is not null)
        {
            throw new FailedToApplyEnvironmentException($"Failed to apply configuration to executable {er.ModelResource.Name}", configuration.Exception);
        }

        // Invoke the debug configuration callback now that endpoints are allocated.
        // This allows launch configurations to access endpoint URLs that were not
        // available during PrepareExecutables().
        // "project" launch types configure their launch configs in PrepareProjectExecutables() directly;
        // all other types (plain executables and project subtypes like azure-functions) are handled here.
        if (!er.ModelResource.HasAnnotationOfType<ForceProcessExecutionAnnotation>()
            && er.ModelResource.SupportsDebugging(_configuration, out var supportsDebuggingAnnotation)
            && supportsDebuggingAnnotation.LaunchConfigurationType is not "project")
        {
            var mode = _configuration[KnownConfigNames.DebugSessionRunMode] ?? ExecutableLaunchMode.NoDebug;
            try
            {
                // Clear any existing launch configurations (needed for restart scenarios).
                exe.Annotate(Executable.LaunchConfigurationsAnnotation, string.Empty);
                supportsDebuggingAnnotation.LaunchConfigurationAnnotator(exe, mode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply launch configuration for resource '{ResourceName}'. Falling back to process execution.", er.ModelResource.Name);
                exe.Spec.ExecutionType = ExecutionType.Process;
            }
        }

        await factory.CreateDcpObjectsAsync([exe], cancellationToken).ConfigureAwait(false);
    }

    private void PrepareProjectExecutables()
    {
        var modelProjectResources = _model.GetProjectResources();

        foreach (var project in modelProjectResources)
        {
            if (!project.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
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
                exe.Spec.Persistent = persistent;
                if (persistent)
                {
                    ApplyMonitorProcess(project, exe.Spec);
                }

                SupportsDebuggingAnnotation? supportsDebuggingAnnotation = null;
                var forceProcessExecution = project.HasAnnotationOfType<ForceProcessExecutionAnnotation>();
                if (!persistent && !forceProcessExecution && project.SupportsDebugging(_configuration, out supportsDebuggingAnnotation))
                {
                    exe.Spec.ExecutionType = ExecutionType.IDE;
                    exe.Spec.FallbackExecutionTypes = [ExecutionType.Process];

                    if (supportsDebuggingAnnotation.LaunchConfigurationType is "project")
                    {
                        // We want this annotation even if we are not using IDE execution; see ToSnapshot() for details.
                        ApplyProjectLaunchConfiguration(exe, project, projectMetadata, supportsDebuggingAnnotation);
                    }
                    // Non-project launch types (e.g. azure-functions) have their launch configuration
                    // applied later in CreateExecutableAsync() after endpoints are allocated,
                    // unless the IDE didn't send DEBUG_SESSION_INFO (handled by the fallback branch below).

                    // File-based apps (.cs files) are not supported by all IDEs (e.g. Visual Studio
                    // returns 500 for them). Populate fallback process args so that when the IDE
                    // rejects the launch request and DCP falls back to ExecutionType.Process, the
                    // executable starts with the correct `dotnet run --file` arguments.
                    if (projectMetadata.IsFileBasedApp)
                    {
                        projectArgs.Add("run");
                        projectArgs.Add("--file");
                        projectArgs.Add(projectMetadata.ProjectPath);
                        projectArgs.Add("--no-cache");
                        if (projectMetadata.SuppressBuild)
                        {
                            projectArgs.Add("--no-build");
                        }
                        projectArgs.Add("--no-launch-profile");

                        if (!string.IsNullOrEmpty(_distributedApplicationOptions.Configuration))
                        {
                            projectArgs.AddRange(new[] { "--configuration", _distributedApplicationOptions.Configuration });
                        }
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
                    exe.Spec.FallbackExecutionTypes = [ExecutionType.Process];

                    ApplyProjectLaunchConfiguration(exe, project, projectMetadata);
                }
                else
                {
                    exe.Spec.ExecutionType = ExecutionType.Process;

                    // Some ProjectResource subtypes, such as MAUI platform resources, intentionally
                    // provide their own executable command and SDK-shaped app host args. Do not prefix
                    // those args with Aspire's default `dotnet run --project ...` wrapper.
                    if (executableAnnotation is null)
                    {
                        var projectLaunchConfiguration = new ProjectLaunchConfiguration();
                        projectLaunchConfiguration.ProjectPath = projectMetadata.ProjectPath;

                        // `dotnet watch` does not work with file-based apps yet, so we have to use `dotnet run` in that case
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
                            projectArgs.AddRange(new[] { "--configuration", _distributedApplicationOptions.Configuration });
                        }

                        // We pretty much always want to suppress the normal launch profile handling
                        // because the settings from the profile will override the ambient environment settings, which is not what we want
                        // (the ambient environment settings for service processes come from the application model
                        // and should be HIGHER priority than the launch profile settings).
                        // This means we need to apply the launch profile settings manually inside CreateExecutableAsync().
                        projectArgs.Add("--no-launch-profile");

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
                exe.Spec.FallbackExecutionTypes = [ExecutionType.Process];
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
                    var existing = Environment.GetEnvironmentVariable("SSL_CERT_DIR");
                    if (!string.IsNullOrEmpty(existing))
                    {
                        dirs.AddRange(existing.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
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

    private static List<LaunchArgument> BuildLaunchArgs(RenderedModelResource<Executable> er, ExecutableSpec spec, IEnumerable<(string Value, bool IsSensitive)> appHostArgs, int executableArgumentStartIndex)
    {
        // Launch args is the final list of args that are displayed in the UI and possibly added to the executable spec.
        // They're built from app host resource model args and any args in the effective launch profile.
        // Follows behavior in the IDE execution spec when in IDE execution mode:
        // https://github.com/microsoft/aspire/blob/main/docs/specs/IDE-execution.md#project-launch-configuration-type-project
        var appHostArgList = appHostArgs.ToList();
        var launchArgs = new List<LaunchArgument>();
        var nextExecutableArgumentIndex = executableArgumentStartIndex;

        LaunchArgument CreateLaunchArgument(string value, bool isSensitive, bool executable, bool display)
        {
            var effectiveArgumentIndex = executable ? nextExecutableArgumentIndex++ : (int?)null;
            return new(value, isSensitive, executable, display, effectiveArgumentIndex);
        }

        // If the executable is a project then include any command line args from the launch profile.
        if (er.ModelResource is ProjectResource project)
        {
            // Args in the launch profile is used when:
            // 1. The project is run as an executable. Launch profile args are combined with app host supplied args.
            // 2. The project is run by the IDE and no app host args are specified.
            if (spec.ExecutionType == ExecutionType.Process || (spec.ExecutionType == ExecutionType.IDE && appHostArgList.Count == 0))
            {
                // When the .NET project is launched from an IDE the launch profile args are automatically added.
                // We still want to display the args in the dashboard so only add them to the custom arg annotations.
                var executableArg = spec.ExecutionType != ExecutionType.IDE;

                var launchProfileArgs = GetLaunchProfileArgs(project.GetEffectiveLaunchProfile()?.LaunchProfile);
                if (launchProfileArgs.Count > 0 && appHostArgList.Count > 0)
                {
                    // If there are app host args, add a double-dash to separate them from the launch args.
                    launchProfileArgs.Insert(0, "--");
                }

                launchArgs.AddRange(launchProfileArgs.Select(a => CreateLaunchArgument(a, isSensitive: false, executableArg, display: true)));
            }
        }
        else if (er.ModelResource is DotnetToolResource)
        {
            var argSeparator = appHostArgList.Select((a, i) => (index: i, value: a.Value))
                .FirstOrDefault(x => x.value == DotnetToolResourceExtensions.ArgumentSeparator);

            var args = appHostArgList.Select((a, i) => (arg: a, display: i > argSeparator.index));
            launchArgs.AddRange(args.Select(x => CreateLaunchArgument(x.arg.Value, x.arg.IsSensitive, executable: true, x.display)));
            return launchArgs;
        }

        // In the situation where args are combined (process execution) the app host args are added after the launch profile args.
        launchArgs.AddRange(appHostArgList.Select(a => CreateLaunchArgument(a.Value, a.IsSensitive, executable: true, display: true)));

        return launchArgs;
    }

    private void AddDotnetRunArgsForExecutableAnnotatedProject(RenderedModelResource<Executable> er, List<LaunchArgument> launchArgs, int executableArgumentStartIndex)
    {
        if (er.ModelResource is not ProjectResource ||
            !er.ModelResource.TryGetLastAnnotation<ExecutableAnnotation>(out var executableAnnotation) ||
            !string.Equals(executableAnnotation.Command, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var runIndex = launchArgs.FindIndex(argument => argument.Executable && string.Equals(argument.Value, "run", StringComparison.Ordinal));
        if (runIndex < 0)
        {
            return;
        }

        List<LaunchArgument>? launchProfileArgs = null;
        if (runIndex > 0 &&
            launchArgs[0].Executable &&
            string.Equals(launchArgs[0].Value, "--", StringComparison.Ordinal))
        {
            // Process execution passes project launch-profile args before "dotnet run", separated by "--".
            // IDE execution already owns the launch profile via ProjectLaunchConfiguration, so keep those
            // values out of the executable argument list while we insert the AppHost configuration flags
            // immediately after "dotnet run".
            launchProfileArgs = launchArgs.GetRange(0, runIndex);
            launchArgs.RemoveRange(0, runIndex);
            runIndex = 0;
        }

        var argsToInsert = new List<string>();
        if (!string.IsNullOrEmpty(_distributedApplicationOptions.Configuration) &&
            !ContainsDotnetRunOption(launchArgs, "--configuration", "-c"))
        {
            argsToInsert.AddRange(["--configuration", _distributedApplicationOptions.Configuration]);
        }

        if (!ContainsDotnetRunOption(launchArgs, "--no-launch-profile") &&
            !ContainsDotnetRunOption(launchArgs, "--launch-profile"))
        {
            argsToInsert.Add("--no-launch-profile");
        }

        if (argsToInsert.Count == 0 && launchProfileArgs is null)
        {
            return;
        }

        // Some ProjectResource subtypes provide the `dotnet run` command through resource args
        // instead of using Aspire's default project wrapper. Keep the SDK-shaped command, but
        // preserve the same AppHost configuration and launch-profile suppression that regular
        // process-launched project resources get.
        if (argsToInsert.Count > 0)
        {
            launchArgs.InsertRange(runIndex + 1, argsToInsert.Select(argument => new LaunchArgument(argument, IsSensitive: false, Executable: true, Display: false, EffectiveArgumentIndex: null)));
        }

        if (launchProfileArgs is not null)
        {
            // Launch profile args were originally before the app host args, separated by `--`.
            // Once this path preserves the caller-provided `dotnet run` command, those args must
            // move after the inserted SDK options so `dotnet run` parses them as application args.
            launchArgs.AddRange(launchProfileArgs);
        }

        ReindexExecutableLaunchArgs(launchArgs, executableArgumentStartIndex);
    }

    private static bool ContainsDotnetRunOption(List<LaunchArgument> launchArgs, params string[] options)
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
    /// <see cref="ExtensionUtils.SupportsDebugging"/>.
    /// </summary>
    private bool ShouldFallBackToIdeExecution(bool isInDebugSession, SupportsDebuggingAnnotation? supportsDebuggingAnnotation, ExecutableAnnotation? executableAnnotation)
    {
        if (!isInDebugSession)
        {
            return false;
        }

        if (executableAnnotation is not null && supportsDebuggingAnnotation?.LaunchConfigurationType is not null and not "project")
        {
            return false;
        }

        if (supportsDebuggingAnnotation is not null && !string.IsNullOrEmpty(_configuration[KnownConfigNames.DebugSessionInfo]))
        {
            return false;
        }

        return true;
    }

    private void ApplyProjectLaunchConfiguration(Executable exe, ProjectResource project, IProjectMetadata projectMetadata, SupportsDebuggingAnnotation? supportsDebuggingAnnotation = null)
    {
        if (supportsDebuggingAnnotation?.LaunchConfigurationType is "project")
        {
            var mode = GetProjectLaunchConfigurationMode();
            exe.Annotate(Executable.LaunchConfigurationsAnnotation, string.Empty);
            supportsDebuggingAnnotation.LaunchConfigurationAnnotator(exe, mode);

            if (!exe.TryGetProjectLaunchConfiguration(out var projectLaunchConfiguration))
            {
                throw new InvalidOperationException($"Project resource '{project.Name}' produced an invalid project launch configuration.");
            }

            ApplyProjectLaunchConfigurationDefaults(projectLaunchConfiguration, project, projectMetadata);
            exe.SetProjectLaunchConfiguration(projectLaunchConfiguration);
            return;
        }

        exe.SetProjectLaunchConfiguration(CreateProjectLaunchConfiguration(project, projectMetadata));
    }

    private ProjectLaunchConfiguration CreateProjectLaunchConfiguration(ProjectResource project, IProjectMetadata projectMetadata)
    {
        var projectLaunchConfiguration = new ProjectLaunchConfiguration();
        projectLaunchConfiguration.ProjectPath = projectMetadata.ProjectPath;
        projectLaunchConfiguration.Mode = GetProjectLaunchConfigurationMode();

        ApplyProjectLaunchConfigurationDefaults(projectLaunchConfiguration, project, projectMetadata);

        return projectLaunchConfiguration;
    }

    private static void ApplyProjectLaunchConfigurationDefaults(ProjectLaunchConfiguration projectLaunchConfiguration, ProjectResource project, IProjectMetadata projectMetadata)
    {
        if (string.IsNullOrEmpty(projectLaunchConfiguration.ProjectPath))
        {
            projectLaunchConfiguration.ProjectPath = projectMetadata.ProjectPath;
        }

        projectLaunchConfiguration.DisableLaunchProfile |= project.TryGetLastAnnotation<ExcludeLaunchProfileAnnotation>(out _);
        // Use the effective launch profile which has fallback logic
        if (!projectLaunchConfiguration.DisableLaunchProfile &&
            string.IsNullOrEmpty(projectLaunchConfiguration.LaunchProfile) &&
            project.GetEffectiveLaunchProfile() is NamedLaunchProfile namedLaunchProfile)
        {
            projectLaunchConfiguration.LaunchProfile = namedLaunchProfile.Name;
        }
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
