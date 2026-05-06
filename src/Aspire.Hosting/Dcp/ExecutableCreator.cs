// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREDOTNETTOOL
#pragma warning disable ASPIREMAUI001

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
    private readonly ILogger<ExecutableCreator> _logger;
    private readonly DcpAppResourceStore _appResources;

    public ExecutableCreator(
        IConfiguration configuration,
        DcpNameGenerator nameGenerator,
        DistributedApplicationModel model,
        DistributedApplicationOptions distributedApplicationOptions,
        DistributedApplicationExecutionContext executionContext,
        Locations locations,
        ILogger<ExecutableCreator> logger,
        DcpAppResourceStore appResources)
    {
        _configuration = configuration;
        _nameGenerator = nameGenerator;
        _model = model;
        _distributedApplicationOptions = distributedApplicationOptions;
        _executionContext = executionContext;
        _locations = locations;
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
        var explicitStartup = resource.ModelResource.TryGetAnnotationsOfType<ExplicitStartupAnnotation>(out _);
        return !explicitStartup;
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

        var launchArgs = BuildLaunchArgs(er, spec, configuration.Arguments, spec.Args?.Count ?? 0);
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

        if (configuration.Exception is not null)
        {
            throw new FailedToApplyEnvironmentException();
        }

        // Invoke the debug configuration callback now that endpoints are allocated.
        // This allows launch configurations to access endpoint URLs that were not
        // available during PrepareExecutables().
        // "project" launch types configure their launch configs in PrepareProjectExecutables() directly;
        // all other types (plain executables and project subtypes like azure-functions) are handled here.
        if (er.ModelResource.SupportsDebugging(_configuration, out var supportsDebuggingAnnotation)
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
                var exe = Executable.Create(exeInstance.Name, "dotnet");
                exe.Spec.WorkingDirectory = Path.GetDirectoryName(projectMetadata.ProjectPath);

                exe.Annotate(CustomResource.OtelServiceNameAnnotation, project.Name);
                exe.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, exeInstance.Suffix);
                exe.Annotate(CustomResource.ResourceNameAnnotation, project.Name);
                exe.Annotate(CustomResource.ResourceReplicaCount, replicas.ToString(CultureInfo.InvariantCulture));
                exe.Annotate(CustomResource.ResourceReplicaIndex, i.ToString(CultureInfo.InvariantCulture));

                DcpExecutor.SetInitialResourceState(project, exe);

                var projectArgs = new List<string>();

                var isInDebugSession = !string.IsNullOrEmpty(_configuration[DcpExecutor.DebugSessionPortVar]);

                if (project.SupportsDebugging(_configuration, out var supportsDebuggingAnnotation))
                {
                    exe.Spec.ExecutionType = ExecutionType.IDE;
                    exe.Spec.FallbackExecutionTypes = [ExecutionType.Process];

                    if (supportsDebuggingAnnotation.LaunchConfigurationType is "project")
                    {
                        // We want this annotation even if we are not using IDE execution; see ToSnapshot() for details.
                        exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, CreateProjectLaunchConfiguration(project, projectMetadata));
                    }
                    // Non-project launch types (e.g. azure-functions) have their launch configuration
                    // applied later in CreateExecutableAsync() after endpoints are allocated,
                    // unless the IDE didn't send DEBUG_SESSION_INFO (handled by the fallback branch below).
                }
                else if (ShouldFallBackToIdeExecution(isInDebugSession, supportsDebuggingAnnotation))
                {
                    // Fall back to IDE execution with a standard ProjectLaunchConfiguration when:
                    // 1. No SupportsDebuggingAnnotation exists (e.g. AddResource-based ProjectResource
                    //    subclasses that don't call WithDebugSupport). These should get the same IDE
                    //    treatment that AddProject provides by default.
                    // 2. The annotation exists but the IDE did not send DEBUG_SESSION_INFO (Visual Studio
                    //    scenario). VS handles all project resources natively, so non-"project" types
                    //    like "azure-functions" still need IDE execution with ProjectLaunchConfiguration.
                    exe.Spec.ExecutionType = ExecutionType.IDE;
                    exe.Spec.FallbackExecutionTypes = [ExecutionType.Process];

                    exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, CreateProjectLaunchConfiguration(project, projectMetadata));
                }
                else
                {
                    exe.Spec.ExecutionType = ExecutionType.Process;

                    var projectLaunchConfiguration = new ProjectLaunchConfiguration();
                    projectLaunchConfiguration.ProjectPath = projectMetadata.ProjectPath;

                    // Check if the resource has a custom launch args override (e.g., MAUI projects using 'dotnet build /t:Run').
                    // When present, the override args replace the standard 'dotnet run' / 'dotnet watch' invocation.
                    if (project.TryGetLastAnnotation<ProjectLaunchArgsOverrideAnnotation>(out var launchOverride))
                    {
                        projectArgs.AddRange(launchOverride.Args);
                        // 'dotnet build' takes the project path as a positional argument (not --project)
                        projectArgs.Add(projectMetadata.ProjectPath);

                        if (!string.IsNullOrEmpty(_distributedApplicationOptions.Configuration))
                        {
                            projectArgs.AddRange(["--configuration", _distributedApplicationOptions.Configuration]);
                        }
                    }
                    // `dotnet watch` does not work with file-based apps yet, so we have to use `dotnet run` in that case
                    else if (_configuration.GetBool("DOTNET_WATCH") is not true || projectMetadata.IsFileBasedApp)
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

                    // 'launchOverride' is declared via out-var in the TryGetLastAnnotation call above;
                    // it is null when the annotation is absent, so this guard prevents double-adding --configuration.
                    if (launchOverride is null && !string.IsNullOrEmpty(_distributedApplicationOptions.Configuration))
                    {
                        projectArgs.AddRange(["--configuration", _distributedApplicationOptions.Configuration]);
                    }

                    // We pretty much always want to suppress the normal launch profile handling
                    // because the settings from the profile will override the ambient environment settings, which is not what we want
                    // (the ambient environment settings for service processes come from the application model
                    // and should be HIGHER priority than the launch profile settings).
                    // This means we need to apply the launch profile settings manually inside CreateExecutableAsync().
                    if (launchOverride is null)
                    {
                        projectArgs.Add("--no-launch-profile");
                    }

                    // We want this annotation even if we are not using IDE execution; see ToSnapshot() for details.
                    exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, projectLaunchConfiguration);
                }

                exe.SetAnnotationAsObjectList(CustomResource.ResourceProjectArgsAnnotation, projectArgs);

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
            exe.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, exeInstance.Suffix);
            exe.Annotate(CustomResource.ResourceNameAnnotation, executable.Name);

            if (executable.SupportsDebugging(_configuration, out _))
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

            DcpExecutor.SetInitialResourceState(executable, exe);

            var exeAppResource = new RenderedModelResource<Executable>(executable, exe);
            DcpModelUtilities.AddServicesProducedInfo(exeAppResource, _appResources.Get());
            _appResources.Add(exeAppResource);
        }
    }

    private async Task<ExecutableConfiguration> BuildExecutableConfiguration(RenderedModelResource<Executable> er, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        var exe = (Executable)er.DcpResource;

        // Build the base paths for certificate output in the DCP session directory.
        var certificatesRootDir = Path.Join(_locations.DcpSessionDir, exe.Metadata.Name);
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
                tlsCertificateConfiguration.Certificate,
                tlsCertificateConfiguration.Password,
                tlsCertificateConfiguration.IsKeyPathReferenced,
                tlsCertificateConfiguration.IsPfxPathReferenced,
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

    /// <summary>
    /// Determines whether to fall back to IDE execution for a project resource that did not
    /// pass <see cref="ExtensionUtils.SupportsDebugging"/>. Returns <see langword="true"/> when
    /// the app host is running inside a debug session and either the resource has no
    /// <see cref="SupportsDebuggingAnnotation"/> (e.g. AddResource-based subclasses) or the
    /// IDE did not send <c>DEBUG_SESSION_INFO</c> (Visual Studio scenario).
    /// </summary>
    private bool ShouldFallBackToIdeExecution(bool isInDebugSession, SupportsDebuggingAnnotation? supportsDebuggingAnnotation)
    {
        if (!isInDebugSession)
        {
            return false;
        }

        if (supportsDebuggingAnnotation is not null && !string.IsNullOrEmpty(_configuration[KnownConfigNames.DebugSessionInfo]))
        {
            return false;
        }

        return true;
    }

    private ProjectLaunchConfiguration CreateProjectLaunchConfiguration(ProjectResource project, IProjectMetadata projectMetadata)
    {
        var projectLaunchConfiguration = new ProjectLaunchConfiguration();
        projectLaunchConfiguration.ProjectPath = projectMetadata.ProjectPath;
        projectLaunchConfiguration.Mode = _configuration[KnownConfigNames.DebugSessionRunMode]
            ?? (Debugger.IsAttached ? ExecutableLaunchMode.Debug : ExecutableLaunchMode.NoDebug);

        projectLaunchConfiguration.DisableLaunchProfile = project.TryGetLastAnnotation<ExcludeLaunchProfileAnnotation>(out _);
        // Use the effective launch profile which has fallback logic
        if (!projectLaunchConfiguration.DisableLaunchProfile && project.GetEffectiveLaunchProfile() is NamedLaunchProfile namedLaunchProfile)
        {
            projectLaunchConfiguration.LaunchProfile = namedLaunchProfile.Name;
        }

        return projectLaunchConfiguration;
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
}
