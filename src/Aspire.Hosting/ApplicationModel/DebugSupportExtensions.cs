// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Utils;
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Extension methods for inspecting whether a resource will be launched by an IDE or extension host
/// for debugging rather than started as a plain process by Aspire.
/// </summary>
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class DebugSupportExtensions
{
    internal const string AspireExtensionBaselineCapability = "baseline.v1";
    private const string AzureFunctionsLaunchConfigurationType = "azure-functions";

    /// <summary>
    /// Determines whether the IDE owns process creation for the resource in the current session.
    /// </summary>
    /// <param name="resource">The resource to inspect.</param>
    /// <param name="configuration">
    /// The app host configuration used to detect the active debug session and its supported launch configuration types.
    /// </param>
    /// <param name="supportsDebuggingAnnotation">
    /// When this method returns <see langword="true"/>, the annotation describing the effective IDE launch. A
    /// compatibility launch returns a synthesized annotation that is not attached to the resource. When this method
    /// returns <see langword="false"/>, this is the resource's last declared annotation, if one exists; that annotation
    /// can describe optional metadata but does not mean the IDE owns process creation.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the IDE owns process creation for this resource; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Integrations use the return value to decide whether to build a runnable process command. When the IDE owns
    /// process creation, commands such as <c>dotnet run --project …</c> or <c>go run …</c> are supplied by the IDE
    /// and must not also be produced by the integration.
    /// </para>
    /// <para>
    /// The declared annotation is selected for IDE launch when a debug session is active, the resource is not forced
    /// to process execution, the resource does not have a persistent lifetime, and the IDE advertised support for the
    /// annotation's launch configuration type. An annotation that requires explicit Debug mode is not selected during
    /// a no-debug session.
    /// </para>
    /// <para>
    /// Project resources have two compatibility rules. First, when an IDE session does not provide a parseable
    /// <c>DEBUG_SESSION_INFO</c> list of supported launch configuration types, Aspire assumes the IDE can launch a
    /// non-file-based project through <see cref="KnownLaunchConfigurationTypes.Project"/>. Such a resource represents
    /// a project file included in the solution currently open in the IDE. A file-based C# app represents a standalone
    /// <c>.cs</c> file rather than a solution project, so its session must explicitly advertise project-launch support.
    /// A session that provides a list without <see cref="KnownLaunchConfigurationTypes.Project"/> cannot use project
    /// launch configurations, so a project resource then uses process execution unless the IDE supports its declared
    /// type.
    /// </para>
    /// <para>
    /// Second, an Azure Functions project normally declares the <c>azure-functions</c> launch configuration type.
    /// When a session advertises <see cref="KnownLaunchConfigurationTypes.Project"/> but not
    /// <c>azure-functions</c>, and the session is not identified as an Aspire extension session by the
    /// <c>baseline.v1</c> capability, Aspire must fall back from <c>azure-functions</c> to
    /// <see cref="KnownLaunchConfigurationTypes.Project"/> so the IDE launches and debugs the project. When either
    /// compatibility rule replaces a missing or unsupported declared annotation, this method returns a synthesized
    /// project annotation that is not attached to the resource. The synthesized annotation does not own launch tool
    /// arguments; see <see cref="HasLaunchToolArgsOwnedBy"/>.
    /// </para>
    /// <para>
    /// A <see cref="ProjectLaunchArgsOverrideAnnotation"/> tells Aspire to execute the complete project command as a
    /// DCP-managed process instead of asking the IDE to create the process. Because this method reports whether the
    /// IDE owns process creation, it returns <see langword="false"/> for that resource. A supported custom launch
    /// annotation can still add metadata to the process-launched resource, but that metadata does not transfer process
    /// ownership to the IDE.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Debug support inspection is a local .NET helper and is not part of the ATS surface.")]
    public static bool SupportsDebugging(this IResource resource, IConfiguration configuration, [NotNullWhen(true)] out SupportsDebuggingAnnotation? supportsDebuggingAnnotation)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configuration);

        var decision = resource.GetEffectiveDebugSupportDecision(configuration);
        supportsDebuggingAnnotation = decision.EffectiveAnnotation ?? decision.DeclaredAnnotation;

        return decision.EffectiveAnnotation is not null;
    }

    internal static EffectiveDebugSupportDecision GetEffectiveDebugSupportDecision(
        this IResource resource,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configuration);

        resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var declaredAnnotation);

        // IDE launch is unavailable without an active debug session. Force-process and persistent resources always
        // stay on the DCP process path, and debug-only producers are inactive during a no-debug session.
        if (string.IsNullOrEmpty(configuration[DcpExecutor.DebugSessionPortVar])
            // Upstream host injects an execution-service endpoint even without a real IDE.
            // CLI ownership keeps builds and executable launches on Aspire's process path.
            || DotnetWatchOwnership.IsCliOwned(configuration[DotnetWatchOwnership.OwnerEnvironmentVariable])
            || resource.HasAnnotationOfType<ForceProcessExecutionAnnotation>()
            || resource.HasPersistentLifetime()
            || (declaredAnnotation?.RequiresDebugMode is true &&
                configuration[KnownConfigNames.DebugSessionRunMode] != ExecutableLaunchMode.Debug))
        {
            return new(
                declaredAnnotation,
                SupportedDeclaredAnnotation: null,
                EffectiveAnnotation: null,
                UsesCompatibilityProjectLaunch: false,
                UsesAzureFunctionsProjectCompatibility: false);
        }

        var debugSessionInfo = configuration[KnownConfigNames.DebugSessionInfo];
        var supportedLaunchConfigurations =
            DebugSessionInfoParser.TryGetSupportedLaunchConfigurations(debugSessionInfo, out var parsedLaunchConfigurations)
                ? parsedLaunchConfigurations
                : null;

        var supportedDeclaredAnnotation =
            declaredAnnotation is not null &&
            IsDeclaredLaunchConfigurationSupported(resource, declaredAnnotation, supportedLaunchConfigurations)
                ? declaredAnnotation
                : null;

        // A project launch override is a complete process command. Retain a supported declared producer only so it
        // can contribute optional metadata; it must not make the resource appear to be IDE-launched.
        if (resource is ProjectResource &&
            resource.HasAnnotationOfType<ProjectLaunchArgsOverrideAnnotation>())
        {
            return new(
                declaredAnnotation,
                supportedDeclaredAnnotation,
                EffectiveAnnotation: null,
                UsesCompatibilityProjectLaunch: false,
                UsesAzureFunctionsProjectCompatibility: false);
        }

        // When the IDE supports the declared launch type, that annotation is both the active producer and the
        // effective configuration sent to the IDE.
        if (supportedDeclaredAnnotation is not null)
        {
            return new(
                declaredAnnotation,
                supportedDeclaredAnnotation,
                EffectiveAnnotation: supportedDeclaredAnnotation,
                UsesCompatibilityProjectLaunch: false,
                UsesAzureFunctionsProjectCompatibility: false);
        }

        // Only project resources that satisfy the legacy compatibility rules can replace an unsupported or absent
        // declared producer with a synthesized native "project" launch configuration.
        if (resource is not ProjectResource ||
            !ShouldUseCompatibilityProjectLaunch(
                resource,
                declaredAnnotation,
                supportedLaunchConfigurations))
        {
            return new(
                declaredAnnotation,
                SupportedDeclaredAnnotation: null,
                EffectiveAnnotation: null,
                UsesCompatibilityProjectLaunch: false,
                UsesAzureFunctionsProjectCompatibility: false);
        }

        // Keep synthesized compatibility metadata separate from the declared producer, and mark it, so it cannot claim
        // ownership of integration-specific launch-tool arguments in either the launch recipe or HasLaunchToolArgsOwnedBy.
        var effectiveAnnotation = SupportsDebuggingAnnotation.Create(
            resource.Name,
            KnownLaunchConfigurationTypes.Project,
            context => Task.FromResult(ProjectLaunchConfigurationFactory.Create(context.Resource, context.Mode)));
        effectiveAnnotation.IsCompatibilityProjectLaunch = true;

        return new(
            declaredAnnotation,
            SupportedDeclaredAnnotation: null,
            EffectiveAnnotation: effectiveAnnotation,
            UsesCompatibilityProjectLaunch: true,
            UsesAzureFunctionsProjectCompatibility:
                declaredAnnotation?.LaunchConfigurationType == AzureFunctionsLaunchConfigurationType);
    }

    /// <summary>
    /// Determines whether the launch configuration performs the resource's tool invocation itself, meaning the
    /// resource's launch tool arguments must not also be passed to the launched program.
    /// </summary>
    /// <param name="resource">The resource to inspect.</param>
    /// <param name="supportsDebuggingAnnotation">The launch configuration annotation to compare.</param>
    /// <returns><see langword="true"/> when the launch configuration supplies the tool invocation; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// This is <see langword="false"/> for a resource whose launch tool arguments declare no owning launch
    /// configuration type, because such a prefix is always passed to the program.
    /// </para>
    /// <para>
    /// Only a launch configuration declared with a <c>WithDebugSupport</c> overload owns launch tool arguments. This is
    /// also <see langword="false"/> for the synthesized project annotation that <see cref="SupportsDebugging"/>
    /// returns for a compatibility launch, because such a launch passes the resource's launch tool arguments to the
    /// launched program.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Debug support inspection is a local .NET helper and is not part of the ATS surface.")]
    public static bool HasLaunchToolArgsOwnedBy(this IResource resource, SupportsDebuggingAnnotation supportsDebuggingAnnotation)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(supportsDebuggingAnnotation);

        // The launch recipe omits owned launch tool arguments only for the active declared producer; a synthesized
        // compatibility annotation never takes that role, so it must not claim ownership here either.
        return !supportsDebuggingAnnotation.IsCompatibilityProjectLaunch
            && resource.TryGetLastAnnotation<LaunchToolArgsCallbackAnnotation>(out var launchToolAnnotation)
            && launchToolAnnotation.OwningLaunchConfigurationType is string owner
            && string.Equals(owner, supportsDebuggingAnnotation.LaunchConfigurationType, StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates the launch configuration that this resource sends to the IDE for the given launch mode.
    /// </summary>
    /// <param name="resource">The resource to inspect. It must carry a <see cref="SupportsDebuggingAnnotation"/>.</param>
    /// <param name="mode">The launch mode, one of the values on <see cref="ExecutableLaunchMode"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The launch configuration, typically an <see cref="ExecutableLaunchConfiguration"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> or <paramref name="mode"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The resource does not declare debug launch support.</exception>
    /// <remarks>
    /// <para>
    /// Launch configuration is created by invoking the producer callback passed to a
    /// <c>WithDebugSupport</c> overload on <see cref="ResourceBuilderExtensions"/>, which owns the complete
    /// configuration; Aspire serializes the result as-is. The configuration is produced fresh on each call.
    /// Aspire may call the producer several times for the same resource.
    /// </para>
    /// <para>
    /// This overload does not resolve the resource's environment variables. A context-based producer receives
    /// an empty environment. Aspire's executable creation path supplies the resolved environment through the
    /// context-aware overload.
    /// </para>
    /// <para>
    /// This describes the launch configuration itself, not whether one is going to be used. Depending on how
    /// the application is started, or how a resource is configured, Aspire may or may not run the resource under
    /// a debugger. Use <see cref="SupportsDebugging"/> to test for that.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Debug support inspection is a local .NET helper and is not part of the ATS surface.")]
    public static Task<object> CreateLaunchConfigurationAsync(
        this IResource resource,
        string mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(mode);

        return resource.CreateLaunchConfigurationAsync(
            new LaunchConfigurationCallbackContext(
                mode,
                resource,
                new Dictionary<string, string>(),
                cancellationToken));
    }

    /// <summary>
    /// Creates the launch configuration that this resource sends to the IDE using a callback context.
    /// </summary>
    /// <param name="resource">The resource to inspect. It must carry a <see cref="SupportsDebuggingAnnotation"/>.</param>
    /// <param name="context">The callback context containing the resolved environment and launch data.</param>
    /// <returns>The launch configuration, typically an <see cref="ExecutableLaunchConfiguration"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="context"/> belongs to a different resource.</exception>
    /// <exception cref="InvalidOperationException">The resource does not declare debug launch support.</exception>
    /// <remarks>
    /// <para>
    /// Launch configuration is created by invoking the producer callback passed to
    /// <see cref="ResourceBuilderExtensions.WithDebugSupport{T, TLaunchConfiguration}(IResourceBuilder{T}, Func{LaunchConfigurationCallbackContext, Task{TLaunchConfiguration}}, string)"/>,
    /// which owns the complete configuration; Aspire serializes the result as-is.
    /// </para>
    /// <para>
    /// This method never resolves environment variables. Aspire creates <paramref name="context"/>
    /// when the active debug-support annotation is producing a launch configuration for an executable creation.
    /// </para>
    /// <para>
    /// This overload is internal because only Aspire constructs callback contexts containing resolved environment
    /// variables. Use the public overload when inspecting a launch configuration outside executable creation.
    /// </para>
    /// </remarks>
    internal static Task<object> CreateLaunchConfigurationAsync(
        this IResource resource,
        LaunchConfigurationCallbackContext context)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        if (!ReferenceEquals(resource, context.Resource))
        {
            throw new ArgumentException(
                $"The launch configuration callback context belongs to resource '{context.Resource.Name}', " +
                $"but launch configuration was requested for resource '{resource.Name}'.",
                nameof(context));
        }

        if (!resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebuggingAnnotation))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not declare debug launch support. " +
                $"Call {nameof(ResourceBuilderExtensions.WithDebugSupport)} on the resource first. " +
                $"Note that it only adds the annotation in run mode.");
        }

        return supportsDebuggingAnnotation.LaunchConfigurationProducer(context);
    }

    private static bool IsDeclaredLaunchConfigurationSupported(
        IResource resource,
        SupportsDebuggingAnnotation declaredAnnotation,
        string[]? supportedLaunchConfigurations)
    {
        // A capability-less IDE session can only launch projects loaded in the solution. A bare .cs file must stay
        // on the Process path unless the IDE explicitly advertises project support.
        if (supportedLaunchConfigurations is null)
        {
            return declaredAnnotation.LaunchConfigurationType == KnownLaunchConfigurationTypes.Project &&
                (!resource.TryGetProjectMetadata(out var projectMetadata) || !projectMetadata.IsFileBasedApp);
        }

        return supportedLaunchConfigurations.Contains(declaredAnnotation.LaunchConfigurationType);
    }

    private static bool ShouldUseCompatibilityProjectLaunch(
        IResource resource,
        SupportsDebuggingAnnotation? declaredAnnotation,
        string[]? supportedLaunchConfigurations)
    {
        var hasExplicitProjectLaunchCapability =
            supportedLaunchConfigurations?.Contains(KnownLaunchConfigurationTypes.Project) is true;

        // An advertised capability list is authoritative for every project resource, including one that declares no
        // launch configuration type. The VS Code extension omits "project" when the C# extension is not installed and
        // rejects a "project" run session it cannot launch, which would leave the resource unstarted. Only a session
        // without a usable list keeps the implicit Visual Studio-compatible project support.
        // See docs/specs/IDE-execution.md ("supported_launch_configurations").
        if (supportedLaunchConfigurations is not null && !hasExplicitProjectLaunchCapability)
        {
            return false;
        }

        // Capability-less IDE sessions can only launch projects loaded in the solution, so bare .cs files stay on
        // the Process path. An IDE that explicitly advertises "project" can launch the same .cs project configuration
        // through this compatibility path even when the resource has no SupportsDebuggingAnnotation.
        if (resource.TryGetProjectMetadata(out var projectMetadata) &&
            projectMetadata.IsFileBasedApp &&
            !hasExplicitProjectLaunchCapability)
        {
            return false;
        }

        if (resource.TryGetLastAnnotation<ExecutableAnnotation>(out _) &&
            declaredAnnotation?.LaunchConfigurationType is not null and not KnownLaunchConfigurationTypes.Project)
        {
            return false;
        }

        // When the IDE advertised an explicit capability list (which already includes "project" at this point),
        // only Azure Functions projects outside Aspire extension sessions fall back to the native "project"
        // launcher. Sessions that omit supported_launch_configurations or provide unparseable DEBUG_SESSION_INFO
        // have a null list and retain implicit Visual Studio-compatible project support.
        if (declaredAnnotation is not null && supportedLaunchConfigurations is not null)
        {
            if (declaredAnnotation.LaunchConfigurationType != AzureFunctionsLaunchConfigurationType)
            {
                return false;
            }

            // Visual Studio's native project system can launch an Azure Functions project even though its DCP
            // endpoint advertises only "project". Aspire VS Code sessions include "baseline.v1" in the same
            // capability list, including leased test sessions and detached CLI children whose extension endpoint
            // was intentionally stripped. For those sessions the advertised launch types are authoritative:
            // without "azure-functions", the generic VS Code project launcher must not replace func host startup.
            if (supportedLaunchConfigurations.Contains(AspireExtensionBaselineCapability))
            {
                return false;
            }
        }

        // A directly added legacy ProjectResource may not declare a launch configuration type at all. Preserve its
        // historical Visual Studio-compatible behavior and treat it as a project launch when the session either
        // advertises "project" or provides no usable capability list. Project v2 resources declare their capability
        // explicitly and do not use this compatibility path.
        return true;
    }
}

/// <summary>
/// Describes the declared producer and effective debug support selected for the current IDE session.
/// </summary>
/// <param name="DeclaredAnnotation">The resource's last declared debug-support annotation, whether supported or not.</param>
/// <param name="SupportedDeclaredAnnotation">
/// The supported declared producer. Only this annotation can own launch-tool arguments or contribute custom metadata.
/// </param>
/// <param name="EffectiveAnnotation">
/// The annotation describing the actual IDE launch, including a synthesized compatibility annotation when applicable.
/// </param>
/// <param name="UsesCompatibilityProjectLaunch">
/// Whether <paramref name="EffectiveAnnotation"/> is synthesized project compatibility metadata rather than a declared producer.
/// </param>
/// <param name="UsesAzureFunctionsProjectCompatibility">
/// Whether launch-profile arguments must be materialized for the Azure Functions compatibility route.
/// </param>
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
internal readonly record struct EffectiveDebugSupportDecision(
    SupportsDebuggingAnnotation? DeclaredAnnotation,
    SupportsDebuggingAnnotation? SupportedDeclaredAnnotation,
    SupportsDebuggingAnnotation? EffectiveAnnotation,
    bool UsesCompatibilityProjectLaunch,
    bool UsesAzureFunctionsProjectCompatibility);
